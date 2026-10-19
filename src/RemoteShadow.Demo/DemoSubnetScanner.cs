using System.Runtime.CompilerServices;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo;

/// <summary>
/// Сканирование подсети в демо-режиме: детерминированное, с паузами через <see cref="Task.Delay"/> для реалистичного
/// прогресс-бара, но БЕЗ единого реального сокета/ICMP-пакета/DNS-запроса. Адреса, уже назначенные демо-компьютерам
/// (<see cref="DemoNetworkMap"/>), «отвечают» так же, как эти компьютеры в <see cref="DemoData"/>; остальные адреса —
/// примерно один из шести — имитируют устройство вне AD (без сопоставления по обратному DNS).
/// </summary>
public sealed class DemoSubnetScanner : ISubnetScanner
{
    private readonly DemoData _data;
    private readonly DemoNetworkMap _map;
    private readonly DemoTimings _timings;
    private readonly TimeProvider _time;

    public DemoSubnetScanner(DemoData data, DemoNetworkMap map, TimeProvider time, DemoTimings? timings = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _timings = timings ?? DemoTimings.Default;
    }

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        SubnetDefinition subnet, IProgress<ScanProgress>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subnet);
        ct.ThrowIfCancellationRequested();

        var addresses = Cidr.Parse(subnet.Cidr).UsableAddresses().Select(a => a.ToString()).ToList();
        var total = addresses.Count;
        var completed = 0;

        foreach (var ip in addresses)
        {
            ct.ThrowIfCancellationRequested();
            completed++;

            // Пауза небольшая и ограниченная сверху: реалистичный прогресс без реальной задержки сети, даже на
            // крупных подсетях (/16 и больше) скан не должен занимать часы.
            var pause = DemoDelay.ByKey($"{ip}:scan", TimeSpan.Zero, _timings.ProbeMax);
            if (pause > TimeSpan.FromMilliseconds(5)) pause = TimeSpan.FromMilliseconds(5);
            await DemoDelay.Wait(pause, ct).ConfigureAwait(false);

            progress?.Report(new ScanProgress(completed, total, ip));

            var host = _map.HostFor(ip);
            var online = host is not null
                ? _data.IsOnline(_data.FindComputerByHost(host)!.Id)
                : DemoRandom.Hash($"{ip}:{subnet.Id}") % 6 == 0;

            if (!online) continue;

            var now = _time.GetUtcNow().UtcDateTime;
            yield return new DiscoveredDevice
            {
                Key = ip,
                SubnetId = subnet.Id,
                IpAddress = ip,
                ReverseDnsName = host,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                FoundInLastScan = true,
                IsOnline = true,
            };
        }
    }
}

/// <summary>
/// Наполняет новые настройки раздела «Компьютеры» демо-данными (избранное, группы, подсети) — только если они ещё
/// пусты, чтобы не перетирать то, что пользователь уже настроил в демо-сессии.
/// </summary>
public static class DemoSettingsSeeding
{
    public static void SeedIfEmpty(AppSettings settings, DemoData data)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(data);

        var computers = data.GetComputers().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (computers.Count == 0) return;

        // Первый запуск демо: включаем проверку ssh (по умолчанию выключена), чтобы фильтр «SSH» было на чём проверить.
        if (settings.FavoriteComputers.Count == 0 && settings.ComputerGroups.Count == 0 && settings.Subnets.Count == 0)
            settings.CheckSsh = true;

        if (settings.FavoriteComputers.Count == 0)
        {
            settings.FavoriteComputers.AddRange(computers.Take(3).Select(c => c.Id));
        }

        if (settings.ComputerGroups.Count == 0)
        {
            var servers = computers.Where(c => (c.OperatingSystem ?? string.Empty).Contains("Server", StringComparison.OrdinalIgnoreCase))
                .Take(5).Select(c => c.Id).ToList();
            var others = computers.Where(c => !servers.Contains(c.Id)).Skip(3).Take(4).Select(c => c.Id).ToList();

            settings.ComputerGroups.Add(new ComputerGroup { Name = "Серверная", ComputerIds = servers });
            settings.ComputerGroups.Add(new ComputerGroup { Name = "Бухгалтерия", ComputerIds = others });
        }

        if (settings.Subnets.Count == 0)
        {
            settings.Subnets.Add(new SubnetDefinition
            {
                Name = "Офисная подсеть", Cidr = DemoNetworkMap.OfficeCidr, ProbeKind = ProbeKind.Both, TimeoutMs = 300, Parallelism = 32,
            });
            settings.Subnets.Add(new SubnetDefinition
            {
                Name = "Гостевой Wi-Fi", Cidr = "10.50.4.0/24", ProbeKind = ProbeKind.IcmpOnly, TimeoutMs = 200, Parallelism = 32,
            });
        }
    }
}
