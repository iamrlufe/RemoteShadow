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

        var range = Cidr.Parse(subnet.Cidr);
        if (range.UsableCount > int.MaxValue)
            throw new ArgumentException($"Подсеть {subnet.Cidr} слишком велика для сканирования.", nameof(subnet));

        // Адреса перечисляются лениво: список строк для /8 занял бы гигабайт памяти.
        var total = (int)range.UsableCount;
        var completed = 0;

        foreach (var address in range.UsableAddresses())
        {
            var ip = address.ToString();
            ct.ThrowIfCancellationRequested();
            completed++;

            // Пауза небольшая и ограниченная сверху: реалистичный прогресс без реальной задержки сети, даже на
            // крупных подсетях (/16 и больше) скан не должен занимать часы.
            var pause = DemoDelay.ByKey($"{ip}:scan", TimeSpan.Zero, _timings.ProbeMax);
            if (pause > TimeSpan.FromMilliseconds(5)) pause = TimeSpan.FromMilliseconds(5);
            await DemoDelay.Wait(pause, ct).ConfigureAwait(false);

            progress?.Report(new ScanProgress(completed, total, ip));

            // Адрес мог быть выдан компьютеру, которого уже нет (удалён или переименован): тогда он просто не отвечает.
            var host = _map.HostFor(ip);
            var online = host is not null
                ? _data.FindComputerByHost(host) is { } computer && _data.IsOnline(computer.Id)
                : DemoRandom.Hash($"{ip}:{subnet.Id}") % 6 == 0;

            if (!online) continue;

            var now = _time.GetUtcNow().UtcDateTime;
            var profile = host is null ? DemoDeviceProfiles.For(ip) : null;
            yield return new DiscoveredDevice
            {
                Key = ip,
                SubnetId = subnet.Id,
                IpAddress = ip,
                ReverseDnsName = host,
                Hostname = host ?? profile?.Hostname,
                NameSource = host is not null ? "DNS" : profile?.Hostname is null ? null : "NetBIOS",
                Mac = profile?.Mac,
                Vendor = profile?.Vendor,
                DeviceKind = profile?.Kind,
                Banner = profile?.Banner,
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

        if (settings.DemoSeeded) return;

        var computers = data.GetComputers().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (computers.Count == 0) return;

        settings.DemoSeeded = true;

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

/// <summary>Правдоподобные устройства вне AD для демо-скана: тип, производитель, MAC и, не всегда, имя. Детерминированно по IP.</summary>
public sealed record DemoDeviceProfile(string Kind, string Vendor, string Mac, string? Hostname, string? Banner, bool WindowsWithWinRm);

public static class DemoDeviceProfiles
{
    private static readonly (string Kind, string Vendor, string Oui, string? NamePrefix, string? Banner, bool Win)[] Templates =
    {
        ("Принтер", "HP Inc.", "3C:D9:2B", "HP-LJ", "HP LaserJet · Server: HP-ChaiSOE", false),
        ("Принтер", "Kyocera Document Solutions", "00:C0:EE", null, "KYOCERA ECOSYS", false),
        ("Камера", "Hangzhou Hikvision", "C0:56:E3", null, "DNVRS-Webs", false),
        ("Сетевое оборудование", "Ubiquiti Inc", "24:5A:4C", "UAP", null, false),
        ("Linux/Unix", "Raspberry Pi Trading Ltd", "DC:A6:32", "rpi", "SSH-2.0-OpenSSH_9.2p1 Debian", false),
        ("Windows", "Dell Inc.", "F8:BC:12", "WS", null, true),
        ("Windows", "Lenovo", "54:E1:AD", "NB", null, true),
        ("Windows", "ASUSTek COMPUTER INC.", "04:D4:C4", null, null, false),
    };

    public static DemoDeviceProfile For(string ip)
    {
        var h = DemoRandom.Hash($"profile:{ip}");
        var t = Templates[(int)(h % (uint)Templates.Length)];
        var tail = $"{(h >> 8) & 0xFF:X2}:{(h >> 16) & 0xFF:X2}:{(h >> 24) & 0xFF:X2}";
        var last = ip[(ip.LastIndexOf('.') + 1)..];
        var name = t.NamePrefix is null ? null : $"{t.NamePrefix}-{last}";
        return new DemoDeviceProfile(t.Kind, t.Vendor, $"{t.Oui}:{tail}", name, t.Banner, t.Win);
    }
}

/// <summary>Демо-сбор сведений по WinRM: правдоподобные данные без единого сетевого запроса.</summary>
public sealed class DemoDeviceInventoryService : IDeviceInventoryService
{
    public async Task<DeviceInventory> CollectAsync(string host, CancellationToken ct)
    {
        await DemoDelay.Wait(TimeSpan.FromMilliseconds(250 + DemoRandom.Hash($"inv:{host}") % 700), ct).ConfigureAwait(false);

        var ip = host.Split('.').Length == 4 && host.All(c => char.IsDigit(c) || c == '.') ? host : null;
        var profile = ip is null ? null : DemoDeviceProfiles.For(ip);
        if (profile is not null && !profile.WindowsWithWinRm) return new DeviceInventory { Error = "WinRM закрыт (порт 5985)" };

        var h = DemoRandom.Hash($"inv:{host}");
        var laptop = h % 2 == 0;
        return new DeviceInventory
        {
            Hostname = profile?.Hostname ?? host.Split('.')[0].ToUpperInvariant(),
            Domain = "WORKGROUP",
            OsName = h % 3 == 0 ? "Microsoft Windows 10 Pro" : "Microsoft Windows 11 Pro",
            OsVersion = h % 3 == 0 ? "10.0.19045 (сборка 19045)" : "10.0.26100 (сборка 26100)",
            Manufacturer = profile?.Vendor ?? "Dell Inc.",
            Model = laptop ? "Latitude 5440" : "OptiPlex 7010",
            SerialNumber = $"{(char)('A' + h % 26)}{h % 10_000_000:D7}",
            Mac = profile?.Mac,
            Details = new Dictionary<string, string>
            {
                ["Процессор"] = "Intel(R) Core(TM) i5-1335U, ядер: 10",
                ["Память"] = laptop ? "16 ГБ" : "32 ГБ",
                ["Диски"] = "C: 476 ГБ (свободно 212 ГБ)",
                ["Пользователь"] = @"WORKGROUP\user",
            },
        };
    }
}
