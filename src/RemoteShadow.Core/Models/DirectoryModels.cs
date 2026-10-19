using RemoteShadow.Core.Util;

namespace RemoteShadow.Core.Models;

/// <summary>Компьютер из AD. Ключ записи — <see cref="Id"/> (objectGUID): не меняется при переименовании и переносе.</summary>
public sealed record ComputerInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? DnsName { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OperatingSystemVersion { get; init; }
    public string? Description { get; init; }
    public required string DistinguishedName { get; init; }

    /// <summary>lastLogonTimestamp, UTC. Приблизительно: отстаёт на 9–14 дней.</summary>
    public DateTime? LastLogon { get; init; }

    /// <summary>whenChanged, UTC.</summary>
    public DateTime? WhenChanged { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>null — ещё не проверяли. Общий статус «в сети»: true, если ответил хотя бы один включённый протокол.</summary>
    public bool? IsOnline { get; init; }

    /// <summary>IP-адреса компьютера (DNS A-записи по <see cref="HostName"/>). null/пусто — ещё не резолвили или не нашли.</summary>
    public IReadOnlyList<string>? IpAddresses { get; init; }

    /// <summary>null — не проверяли этот протокол (см. настройки «какие протоколы включены»).</summary>
    public bool? RdpAvailable { get; init; }

    public bool? SmbAvailable { get; init; }

    public bool? SshAvailable { get; init; }

    public bool? IcmpAvailable { get; init; }

    /// <summary>Когда последний раз проверялась доступность (UTC). null — ни разу.</summary>
    public DateTime? LastCheckedUtc { get; init; }

    /// <summary>true — проверка доступности выполняется прямо сейчас (для статуса «Checking» в UI).</summary>
    public bool IsChecking { get; init; }

    /// <summary>
    /// Статус для UI: Checking, пока идёт проверка; Unknown, если проверок ещё не было; иначе Online/Offline
    /// по <see cref="IsOnline"/>.
    /// </summary>
    public AvailabilityStatus AvailabilityStatus => IsChecking
        ? AvailabilityStatus.Checking
        : IsOnline switch { true => AvailabilityStatus.Online, false => AvailabilityStatus.Offline, null => AvailabilityStatus.Unknown };

    /// <summary>Имя для подключения: полное DNS-имя (нужно для Kerberos), иначе короткое.</summary>
    public string HostName => string.IsNullOrWhiteSpace(DnsName) ? Name : DnsName;

    /// <summary>DN подразделения или контейнера, в котором лежит компьютер.</summary>
    public string ParentDn => Dn.Parent(DistinguishedName);
}

/// <summary>Статус доступности компьютера/устройства для UI (цвет: зелёный/красный/жёлтый/серый).</summary>
public enum AvailabilityStatus
{
    /// <summary>Серый — проверок ещё не было.</summary>
    Unknown,

    /// <summary>Жёлтый — проверка идёт прямо сейчас.</summary>
    Checking,

    /// <summary>Зелёный — ответил хотя бы один включённый протокол.</summary>
    Online,

    /// <summary>Красный — ни один включённый протокол не ответил.</summary>
    Offline,
}

/// <summary>Узел дерева подразделений (OU или контейнер).</summary>
public sealed record OuNode(string DistinguishedName, string Name, bool HasChildren = true);

/// <summary>Условия выборки компьютеров из AD.</summary>
/// <param name="ChangedSince">Только изменённые с этого момента (UTC); null — все.</param>
/// <param name="IncludeDisabled">Включать отключённые учётные записи.</param>
public sealed record ComputerQuery(DateTime? ChangedSince = null, bool IncludeDisabled = false);

public sealed record UserInfo
{
    public required Guid Id { get; init; }
    public required string SamAccountName { get; init; }
    public required string DistinguishedName { get; init; }
    public string? DisplayName { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? Department { get; init; }
    public string? Title { get; init; }
    public string? Phone { get; init; }
    public string? Mail { get; init; }
    public bool Enabled { get; init; } = true;
    public bool LockedOut { get; init; }
    public bool PasswordNeverExpires { get; init; }
    public DateTime? PasswordLastSet { get; init; }
    public DateTime? PasswordExpires { get; init; }
    public DateTime? LastLogon { get; init; }

    /// <summary>Что показывать в списках: ФИО, иначе логин.</summary>
    public string ShownName => string.IsNullOrWhiteSpace(DisplayName) ? SamAccountName : DisplayName;
}

public sealed record GroupInfo(string Name, string DistinguishedName, string? Description = null);
