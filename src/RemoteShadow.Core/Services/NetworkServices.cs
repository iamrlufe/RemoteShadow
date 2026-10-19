using RemoteShadow.Core.Models;

namespace RemoteShadow.Core.Services;

/// <summary>Проверка доступности узла по ICMP (ping). Реализация в RemoteShadow.Windows.Hosts, фейк — в Demo/тестах.</summary>
public interface IIcmpProbe
{
    /// <summary>true — узел ответил на ping за отведённое время. Любая ошибка (нет маршрута, запрещено фаерволом) → false.</summary>
    Task<bool> PingAsync(string hostOrIp, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Резолвинг DNS-имени компьютера в IP-адреса. DNS-запись не означает доступность — это только адрес.</summary>
public interface IDnsResolverService
{
    /// <summary>Все адреса IPv4, под которыми разрешился хост; пусто, если имя не резолвится.</summary>
    Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Обратный DNS (PTR): IP → имя хоста, если есть однозначная запись.</summary>
public interface IReverseDnsResolver
{
    /// <summary>null — записи нет, не разрешилась или конфликтует (несколько разных имён не считаются однозначными).</summary>
    Task<string?> ResolveAsync(string ipAddress, CancellationToken ct);
}

/// <summary>
/// Многопротокольная проверка доступности (ICMP/RDP/SMB/SSH, по настройкам) — замена/расширение
/// <see cref="IOnlineStatusService"/>: отдельный флаг на каждый протокол и общий статус Online/Offline/Checking/Unknown.
/// </summary>
public interface IComputerAvailabilityService
{
    /// <summary>
    /// Проверяет компьютеры (только переданные — обычно видимая часть списка, не вся сеть). Перед проверкой каждого
    /// компьютера индекс получает <see cref="ComputerInfo.IsChecking"/>=true, после — результат по протоколам.
    /// </summary>
    Task RefreshAsync(IReadOnlyCollection<ComputerInfo> computers, bool force, CancellationToken ct);
}

/// <summary>Прогресс сканирования подсети: сколько адресов обработано из скольких.</summary>
public sealed record ScanProgress(int Completed, int Total, string? CurrentAddress);

/// <summary>
/// Сканирование подсети (ICMP+TCP по её <see cref="SubnetDefinition.ProbeKind"/>). Запускается только по явному
/// действию пользователя — никогда автоматически. Параллелизм/таймаут из <see cref="SubnetDefinition"/>.
/// </summary>
public interface ISubnetScanner
{
    /// <summary>
    /// Отдаёт устройства по мере обнаружения ответа; адреса, не ответившие ни на один протокол, в выдачу не попадают
    /// (их фиксирует вызывающий — обновляя историю подсети статусом «не найден»). Отмена через <paramref name="ct"/>.
    /// </summary>
    IAsyncEnumerable<DiscoveredDevice> ScanAsync(SubnetDefinition subnet, IProgress<ScanProgress>? progress, CancellationToken ct);
}

/// <summary>Итог слияния устройства, найденного сканированием, с компьютером AD.</summary>
public enum DeviceMatchKind
{
    /// <summary>Однозначное совпадение обратного DNS с HostName/DNS-именем компьютера AD.</summary>
    Matched,

    /// <summary>Обратный DNS не разрешился, конфликтует или не совпал ни с одним компьютером AD — отдельная запись.</summary>
    Unmatched,
}

/// <summary>
/// Сопоставление устройств, найденных сканированием, с компьютерами AD. Сопоставление идёт по обратному DNS-имени,
/// а не по IP (IP может случайно совпасть или устареть в DHCP). Если обратный DNS неоднозначен — запись остаётся
/// отдельной, устройством вне AD.
/// </summary>
public interface IDeviceMergeService
{
    /// <summary>
    /// Для каждого обнаруженного устройства определяет совпадающий компьютер AD (или null). Устройство с
    /// <see cref="DiscoveredDevice.ReverseDnsName"/> == null, а также с именем, под которым в AD числится больше
    /// одного компьютера (конфликт, не должно происходить, но проверяется явно), остаётся несопоставленным.
    /// </summary>
    Guid? Match(DiscoveredDevice device, IReadOnlyList<ComputerInfo> adComputers);
}


/// <summary>Результат опознания устройства по сети без учётных данных.</summary>
public sealed record DeviceFingerprint
{
    public string? Hostname { get; init; }
    public string? NameSource { get; init; }
    public string? Workgroup { get; init; }
    public string? Mac { get; init; }
    public string? Vendor { get; init; }
    public string? DeviceKind { get; init; }
    public string? Banner { get; init; }
}

/// <summary>Опознаёт устройство без учётных данных: имя (NetBIOS), MAC, производитель, тип, баннеры служб.</summary>
public interface IDeviceFingerprinter
{
    /// <summary>Никогда не бросает исключений, кроме отмены: недоступные источники просто не дают данных.</summary>
    Task<DeviceFingerprint> ProbeAsync(string ipAddress, TimeSpan timeout, CancellationToken ct);
}

/// <summary>База производителей по OUI (первые 3 байта MAC).</summary>
public interface IOuiDatabase
{
    /// <summary>Принимает MAC в любом распространённом формате; null — производитель неизвестен.</summary>
    string? Lookup(string mac);
}

/// <summary>MAC по IP из ARP: работает только в своей L2-сети, через маршрутизатор MAC не виден.</summary>
public interface IArpResolver
{
    string? TryGetMac(string ipAddress);
}
