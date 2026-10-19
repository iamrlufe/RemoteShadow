using System.Collections.Concurrent;
using System.Globalization;
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
    /// <remarks>
    /// Не только SMB/RDP/SSH: серверы домена часто закрывают их файрволом, но отвечают на RPC (135), WinRM (5985),
    /// LDAP/DNS (389, 53), веб (80, 443) или SQL (1433). Адрес считается живым, если открыт хотя бы один порт.
    /// </remarks>
    private static readonly (int Port, string Name)[] TcpPorts =
    {
        (445, "smb"), (3389, "rdp"), (22, "ssh"), (135, "rpc"), (5985, "winrm"), (80, "http"), (443, "https"),
        (389, "ldap"), (53, "dns"), (1433, "mssql"),
    };

    private readonly IHostProbe _tcpProbe;
    private readonly IIcmpProbe _icmpProbe;
    private readonly IReverseDnsResolver _reverseDns;
    private readonly TimeProvider _time;
    private readonly IDeviceFingerprinter? _fingerprinter;

    public SubnetScanner(IHostProbe tcpProbe, IIcmpProbe icmpProbe, IReverseDnsResolver reverseDns, TimeProvider time,
        IDeviceFingerprinter? fingerprinter = null)
    {
        _fingerprinter = fingerprinter;
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

        // Прогресс считается в int; /0 и /1 (больше 2^31 адресов) честно отклоняем, а не переполняем счётчики.
        if (range.UsableCount > int.MaxValue)
            throw new ArgumentException($"Подсеть {subnet.Cidr} слишком велика для сканирования.", nameof(subnet));

        // Адреса не собираются в список: у /8 их 16 млн, строки заняли бы гигабайт памяти. Адрес считается по номеру.
        var total = (int)range.UsableCount;
        var first = range.PrefixLength >= 31 ? range.Network : range.Network + 1;
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(subnet.TimeoutMs, 50, 30_000));
        var parallelism = Math.Clamp(subnet.Parallelism, 1, 512);

        var results = new ConcurrentQueue<DiscoveredDevice>();
        var probed = 0;
        var next = -1;

        // Свой источник отмены: если потребитель перестал читать (break, исключение), воркеры не должны продолжать
        // сканировать всю подсеть в фоне.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;

        var workers = Enumerable.Range(0, Math.Min(parallelism, Math.Max(total, 1)))
            .Select(_ => Task.Run(
                () => WorkAsync(first, total, subnet, timeout, results, () => Interlocked.Increment(ref next), () => Interlocked.Increment(ref probed), token),
                CancellationToken.None))
            .ToArray();

        try
        {
            // Отдаём результаты по мере появления, не дожидаясь конца всего скана: UI может показывать прогресс и список сразу.
            var reported = -1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var allDone = workers.All(w => w.IsCompleted);

                while (results.TryDequeue(out var device))
                {
                    ct.ThrowIfCancellationRequested();
                    reported = Volatile.Read(ref probed);
                    progress?.Report(new ScanProgress(reported, total, device.IpAddress));
                    yield return device;
                }

                // Прогресс — по проверенным адресам, а не по найденным устройствам: иначе на пустой подсети он стоит на нуле.
                var done = Volatile.Read(ref probed);
                if (done != reported && !allDone)
                {
                    reported = done;
                    progress?.Report(new ScanProgress(done, total, null));
                }

                if (allDone && results.IsEmpty) break;
                if (!allDone) await Task.Delay(20, ct).ConfigureAwait(false);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            progress?.Report(new ScanProgress(total, total, null));
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task WorkAsync(
        uint first, int total, SubnetDefinition subnet, TimeSpan timeout,
        ConcurrentQueue<DiscoveredDevice> results, Func<int> takeNext, Func<int> markProbed, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var i = takeNext();
            if (i >= total) return;

            var ip = AddressAt(first, i);
            bool online;
            try
            {
                online = await ProbeAsync(ip, subnet.ProbeKind, timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (!online)
            {
                markProbed();
                continue;
            }

            string? reverseDns;
            try
            {
                reverseDns = await _reverseDns.ResolveAsync(ip, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                reverseDns = null; // Сбой обратного DNS не отменяет находку и не роняет весь скан.
            }

            DeviceFingerprint? fp = null;
            if (_fingerprinter is not null)
            {
                try
                {
                    fp = await _fingerprinter.ProbeAsync(ip, timeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    fp = null; // Опознание — дополнение: сбой не отменяет находку.
                }
            }

            markProbed();
            var now = _time.GetUtcNow().UtcDateTime;
            results.Enqueue(new DiscoveredDevice
            {
                Key = ip,
                SubnetId = subnet.Id,
                IpAddress = ip,
                ReverseDnsName = reverseDns,
                Hostname = reverseDns ?? fp?.Hostname,
                NameSource = reverseDns is not null ? "DNS" : fp?.NameSource,
                Workgroup = fp?.Workgroup,
                Mac = fp?.Mac,
                Vendor = fp?.Vendor,
                DeviceKind = fp?.DeviceKind,
                Banner = fp?.Banner,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                FoundInLastScan = true,
                IsOnline = true,
            });
        }
    }

    private static string AddressAt(uint first, int index)
    {
        var value = first + (uint)index;
        return string.Create(CultureInfo.InvariantCulture, $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}");
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

        var name = !string.IsNullOrWhiteSpace(device.ReverseDnsName) ? device.ReverseDnsName : device.Hostname;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var shortName = ShortName(name);
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
