using System.Collections.Concurrent;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Многопротокольная проверка доступности: ICMP ping, TCP RDP/SMB/SSH (какие включены и на каких портах — из
/// <see cref="AppSettings"/>). Компьютер считается «в сети», если ответил хотя бы один включённый протокол.
/// Структура (параллелизм/таймаут/пачки результатов раз в 200 мс/отмена) скопирована с <see cref="OnlineStatusService"/>,
/// чтобы не плодить два разных стиля опроса; этот сервис — его многопротокольная замена для UI «Компьютеры».
/// <para>
/// Статус «идёт проверка» (<see cref="ComputerInfo.IsChecking"/>) проставляется в момент, когда конкретный компьютер
/// реально берётся в работу (а не для всей пачки сразу): до этого он остаётся Unknown, если проверок ещё не было.
/// </para>
/// </summary>
public sealed class ComputerAvailabilityService : IComputerAvailabilityService
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    private readonly IComputerIndex _index;
    private readonly IHostProbe _tcpProbe;
    private readonly IIcmpProbe _icmpProbe;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, DateTime> _checkedAt = new();

    public ComputerAvailabilityService(IComputerIndex index, IHostProbe tcpProbe, IIcmpProbe icmpProbe, ISettingsStore settings, TimeProvider time)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _tcpProbe = tcpProbe ?? throw new ArgumentNullException(nameof(tcpProbe));
        _icmpProbe = icmpProbe ?? throw new ArgumentNullException(nameof(icmpProbe));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task RefreshAsync(IReadOnlyCollection<ComputerInfo> computers, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computers);
        ct.ThrowIfCancellationRequested();

        var settings = _settings.Load();
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(settings.AvailabilityProbeTimeoutMs, 50, 30_000));
        var parallelism = Math.Clamp(settings.AvailabilityParallelism, 1, 512);
        var cacheTtl = TimeSpan.FromMinutes(Math.Max(0, settings.AvailabilityAutoRefreshMinutes));
        var protocols = new ProtocolSettings(settings);

        var todo = SelectStale(computers, force, cacheTtl);
        if (todo.Count == 0) return;

        // Результаты применяются напрямую через IComputerIndex.SetAvailability — индекс сам сливает частые события
        // Changed (RaiseChangedThrottled), поэтому отдельный батчер результатов (как в OnlineStatusService) не нужен.
        var next = -1;
        var workers = Enumerable.Range(0, Math.Min(parallelism, todo.Count))
            .Select(_ => Task.Run(() => WorkAsync(todo, timeout, protocols, () => Interlocked.Increment(ref next), ct), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private List<ComputerInfo> SelectStale(IReadOnlyCollection<ComputerInfo> computers, bool force, TimeSpan cacheTtl)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var seen = new HashSet<Guid>();
        var todo = new List<ComputerInfo>(computers.Count);

        foreach (var computer in computers)
        {
            if (!computer.Enabled || !seen.Add(computer.Id)) continue;

            if (!force && cacheTtl > TimeSpan.Zero
                       && _checkedAt.TryGetValue(computer.Id, out var checkedAt) && now - checkedAt < cacheTtl)
            {
                var known = _index.Find(computer.Id);
                if (known?.LastCheckedUtc is not null) continue;
            }

            todo.Add(computer);
        }

        return todo;
    }

    private async Task WorkAsync(List<ComputerInfo> todo, TimeSpan timeout, ProtocolSettings protocols, Func<int> takeNext, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var i = takeNext();
            if (i >= todo.Count) return;

            var computer = todo[i];
            _index.SetAvailability(computer.Id, isChecking: true);

            bool? rdp = null, smb = null, ssh = null, icmp = null;
            try
            {
                if (!HostNameValidator.IsValid(computer.HostName))
                {
                    rdp = smb = ssh = icmp = false;
                }
                else
                {
                    var host = computer.HostName;

                    var rdpTask = protocols.CheckRdp ? SafeProbeAsync(() => _tcpProbe.IsPortOpenAsync(host, protocols.RdpPort, timeout, ct), ct) : null;
                    var smbTask = protocols.CheckSmb ? SafeProbeAsync(() => _tcpProbe.IsPortOpenAsync(host, protocols.SmbPort, timeout, ct), ct) : null;
                    var sshTask = protocols.CheckSsh ? SafeProbeAsync(() => _tcpProbe.IsPortOpenAsync(host, protocols.SshPort, timeout, ct), ct) : null;
                    var icmpTask = protocols.CheckIcmp ? SafeProbeAsync(() => _icmpProbe.PingAsync(host, timeout, ct), ct) : null;

                    var pending = new List<Task>();
                    if (rdpTask is not null) pending.Add(rdpTask);
                    if (smbTask is not null) pending.Add(smbTask);
                    if (sshTask is not null) pending.Add(sshTask);
                    if (icmpTask is not null) pending.Add(icmpTask);

                    if (pending.Count > 0) await Task.WhenAll(pending).ConfigureAwait(false);

                    if (rdpTask is not null) rdp = await rdpTask.ConfigureAwait(false);
                    if (smbTask is not null) smb = await smbTask.ConfigureAwait(false);
                    if (sshTask is not null) ssh = await sshTask.ConfigureAwait(false);
                    if (icmpTask is not null) icmp = await icmpTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            var online = rdp == true || smb == true || ssh == true || icmp == true;
            var checkedAtUtc = _time.GetUtcNow().UtcDateTime;
            _checkedAt[computer.Id] = checkedAtUtc;

            _index.SetAvailability(computer.Id, isChecking: false, online: online, rdp: rdp, smb: smb, ssh: ssh, icmp: icmp, checkedAtUtc: checkedAtUtc);
        }
    }

    private static async Task<bool> SafeProbeAsync(Func<Task<bool>> probe, CancellationToken ct)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // Сбой одного протокола (DNS/сеть/фаервол) не должен отменять проверку остальных.
        }
    }

    private readonly record struct ProtocolSettings(bool CheckRdp, bool CheckSmb, bool CheckSsh, bool CheckIcmp, int RdpPort, int SmbPort, int SshPort)
    {
        public ProtocolSettings(AppSettings settings)
            : this(settings.CheckRdp, settings.CheckSmb, settings.CheckSsh, settings.CheckIcmp, settings.RdpPort, settings.SmbPort, settings.SshPort)
        {
        }
    }
}
