using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Сканирует подсеть по её CIDR (<see cref="Cidr"/>): ICMP и/или TCP (общие порты Windows/Linux-служб), в зависимости
/// от <see cref="SubnetDefinition.ProbeKind"/>, с параллелизмом и таймаутом из самой подсети. Запускается только по
/// явному вызову (кнопка «Сканировать» в UI) — сервис сам по себе ничего не делает периодически.
/// Не отвечающие адреса в выдачу не попадают: историю (устройство видели раньше, сейчас не отвечает) ведёт вызывающий.
/// </summary>
public sealed class SubnetScanner : ISubnetScanner
{
    /// <summary>Порты, по которым определяется «устройство в сети», когда включена TCP-проверка.</summary>
    private static readonly (int Port, string Name)[] TcpPorts = { (445, "smb"), (3389, "rdp"), (22, "ssh") };

    private readonly IHostProbe _tcpProbe;
    private readonly IIcmpProbe _icmpProbe;
    private readonly IReverseDnsResolver _reverseDns;
    private readonly TimeProvider _time;

    public SubnetScanner(IHostProbe tcpProbe, IIcmpProbe icmpProbe, IReverseDnsResolver reverseDns, TimeProvider time)
    {
        _tcpProbe = tcpProbe ?? throw new ArgumentNullException(nameof(tcpProbe));
        _icmpProbe = icmpProbe ?? throw new ArgumentNullException(nameof(icmpProbe));
        _reverseDns = reverseDns ?? throw new ArgumentNullException(nameof(reverseDns));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        SubnetDefinition subnet, IProgress<ScanProgress>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subnet);
        ct.ThrowIfCancellationRequested();

        var range = Cidr.Parse(subnet.Cidr);
        var addresses = range.UsableAddresses().Select(a => a.ToString()).ToList();
        var total = addresses.Count;
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(subnet.TimeoutMs, 50, 30_000));
        var parallelism = Math.Clamp(subnet.Parallelism, 1, 512);

        var results = new ConcurrentQueue<DiscoveredDevice>();
        var completed = 0;
        var next = -1;

        var workers = Enumerable.Range(0, Math.Min(parallelism, Math.Max(total, 1)))
            .Select(_ => Task.Run(() => WorkAsync(addresses, subnet, timeout, results, () => Interlocked.Increment(ref next), ct), CancellationToken.None))
            .ToArray();

        // Отдаём результаты по мере появления, не дожидаясь конца всего скана: UI может показывать прогресс и список сразу.
        while (!workers.All(w => w.IsCompleted) || !results.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();

            while (results.TryDequeue(out var device))
            {
                ct.ThrowIfCancellationRequested();
                completed++;
                progress?.Report(new ScanProgress(completed, total, device.IpAddress));
                yield return device;
            }

            if (!workers.All(w => w.IsCompleted)) await Task.Delay(20, ct).ConfigureAwait(false);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        // Воркеры считают прогресс и для адресов, которые не ответили (чтобы прогресс-бар не "застревал"), — их
        // число отличается от количества выданных устройств, поэтому финальный отчёт выравнивает прогресс до 100%.
        progress?.Report(new ScanProgress(total, total, null));
    }

    private async Task WorkAsync(
        List<string> addresses, SubnetDefinition subnet, TimeSpan timeout,
        ConcurrentQueue<DiscoveredDevice> results, Func<int> takeNext, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var i = takeNext();
            if (i >= addresses.Count) return;

            var ip = addresses[i];
            var online = await ProbeAsync(ip, subnet.ProbeKind, timeout, ct).ConfigureAwait(false);
            if (!online) continue;

            string? reverseDns = null;
            try
            {
                reverseDns = await _reverseDns.ResolveAsync(ip, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            results.Enqueue(new DiscoveredDevice
            {
                Key = ip,
                SubnetId = subnet.Id,
                IpAddress = ip,
                ReverseDnsName = reverseDns,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                FoundInLastScan = true,
                IsOnline = true,
            });
        }
    }

    private async Task<bool> ProbeAsync(string ip, ProbeKind kind, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            if (kind == ProbeKind.IcmpOnly) return await _icmpProbe.PingAsync(ip, timeout, ct).ConfigureAwait(false);

            if (kind == ProbeKind.TcpOnly) return await AnyTcpPortOpenAsync(ip, timeout, ct).ConfigureAwait(false);

            // Both: ICMP и TCP параллельно, достаточно одного ответа.
            var icmpTask = _icmpProbe.PingAsync(ip, timeout, ct);
            var tcpTask = AnyTcpPortOpenAsync(ip, timeout, ct);
            await Task.WhenAll(icmpTask, tcpTask).ConfigureAwait(false);
            return icmpTask.Result || tcpTask.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> AnyTcpPortOpenAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        var tasks = TcpPorts.Select(p => _tcpProbe.IsPortOpenAsync(ip, p.Port, timeout, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Any(r => r);
    }
}

/// <summary>
/// Сопоставление устройства, найденного сканированием, с компьютером AD по обратному DNS-имени (а не по IP).
/// Сравнение регистронезависимое, по короткому имени (до первой точки) — AD хранит компьютер и с коротким, и с
/// полным DNS-именем, а PTR обычно отдаёт FQDN. Если короткое имя совпало у нескольких компьютеров AD (не должно
/// происходить при уникальных именах компьютеров, но проверяется явно) — считаем неоднозначным и не сопоставляем.
/// </summary>
public sealed class DeviceMergeService : IDeviceMergeService
{
    public Guid? Match(DiscoveredDevice device, IReadOnlyList<ComputerInfo> adComputers)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(adComputers);

        if (string.IsNullOrWhiteSpace(device.ReverseDnsName)) return null;

        var shortName = ShortName(device.ReverseDnsName);
        if (shortName.Length == 0) return null;

        ComputerInfo? match = null;
        foreach (var computer in adComputers)
        {
            var candidates = new[] { ShortName(computer.Name), ShortName(computer.DnsName ?? string.Empty) };
            if (!candidates.Any(c => c.Length > 0 && string.Equals(c, shortName, StringComparison.OrdinalIgnoreCase))) continue;

            if (match is not null && match.Id != computer.Id) return null; // конфликт: имя совпало с двумя разными компьютерами AD
            match = computer;
        }

        return match?.Id;
    }

    private static string ShortName(string name) => name.Trim().TrimEnd('.').Split('.')[0];
}
