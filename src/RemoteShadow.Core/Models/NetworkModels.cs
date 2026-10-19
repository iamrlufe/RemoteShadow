namespace RemoteShadow.Core.Models;

/// <summary>Какие протоколы проверять при опросе подсети/компьютера.</summary>
public enum ProbeKind
{
    /// <summary>Только TCP (RDP/SMB/SSH — какие включены в настройках).</summary>
    TcpOnly,

    /// <summary>Только ICMP ping.</summary>
    IcmpOnly,

    /// <summary>ICMP и TCP вместе — доступен, если ответил любой.</summary>
    Both,
}

/// <summary>
/// Пользовательская группа компьютеров (локальная метка, не связана с AD): набор компьютеров AD по <see cref="ComputerIds"/>
/// и/или устройств, найденных сканированием подсети, по <see cref="DiscoveredDeviceKeys"/> (<see cref="DiscoveredDevice.Key"/>).
/// Удаление группы не удаляет компьютер из AD.
/// </summary>
public sealed class ComputerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<Guid> ComputerIds { get; set; } = new();
    public List<string> DiscoveredDeviceKeys { get; set; } = new();
}

/// <summary>Подсеть для сканирования: название, CIDR и параметры проверки.</summary>
public sealed class SubnetDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Cidr { get; set; } = string.Empty;
    public ProbeKind ProbeKind { get; set; } = ProbeKind.Both;
    public int TimeoutMs { get; set; } = 500;
    public int Parallelism { get; set; } = 64;
}

/// <summary>
/// Устройство, обнаруженное сканированием подсети. Ключ (<see cref="Key"/>) — IP-адрес в рамках подсети: по нему
/// обновляется запись при повторном скане (чтобы не плодить дубликаты), а не удаляется история при временной
/// недоступности (см. <see cref="LastSeenOnline"/> и <see cref="FoundInLastScan"/>).
/// </summary>
public sealed record DiscoveredDevice
{
    /// <summary>Ключ записи в рамках подсети: IP-адрес.</summary>
    public required string Key { get; init; }

    public required Guid SubnetId { get; init; }

    public required string IpAddress { get; init; }

    /// <summary>Имя из обратного DNS (PTR), если разрешилось однозначно; null — не разрешилось или конфликт.</summary>
    public string? ReverseDnsName { get; init; }

    /// <summary>Компьютер AD, с которым сопоставлено устройство (по совпадению DNS-имени); null — вне AD.</summary>
    public Guid? MatchedComputerId { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public DateTime LastSeenUtc { get; init; }

    /// <summary>Отвечал ли на последнем скане.</summary>
    public bool FoundInLastScan { get; init; }

    /// <summary>Отвечал ли ICMP/TCP (для построения <see cref="FoundInLastScan"/> на верхнем уровне).</summary>
    public bool IsOnline { get; init; }
}

/// <summary>Фильтр по статусу доступности в разделе «Компьютеры».</summary>
public enum AvailabilityFilter
{
    All,
    Online,
    Offline,
    Unknown,
}

/// <summary>Источник записи: из AD, найден сканированием подсети, или оба списка сразу.</summary>
public enum ComputerSourceFilter
{
    All,
    Ad,
    Scan,
}

public enum ComputersSortBy
{
    Availability,
    Name,
    IpAddress,
    LastChecked,
}

/// <summary>
/// Сохраняемые между запусками фильтры и сортировка раздела «Компьютеры» (<see cref="AppSettings.ComputersFilter"/>).
/// </summary>
public sealed class ComputersFilterState
{
    public AvailabilityFilter Availability { get; set; } = AvailabilityFilter.All;
    public ComputerSourceFilter Source { get; set; } = ComputerSourceFilter.All;

    public string NameFilter { get; set; } = string.Empty;
    public string IpFilter { get; set; } = string.Empty;
    public string DescriptionFilter { get; set; } = string.Empty;
    public string OuFilter { get; set; } = string.Empty;

    /// <summary>null — не фильтровать по этому протоколу; true/false — только доступные/недоступные по нему.</summary>
    public bool? RdpFilter { get; set; }
    public bool? SmbFilter { get; set; }
    public bool? SshFilter { get; set; }

    public bool ShowDisabledAd { get; set; }

    public ComputersSortBy SortBy { get; set; } = ComputersSortBy.Availability;
    public bool SortDescending { get; set; }
}
