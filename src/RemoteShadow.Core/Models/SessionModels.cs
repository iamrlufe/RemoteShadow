namespace RemoteShadow.Core.Models;

/// <summary>Состояние сессии (WTS_CONNECTSTATE_CLASS).</summary>
public enum SessionState
{
    Unknown,
    Active,
    Connected,
    ConnectQuery,
    Shadow,
    Disconnected,
    Idle,
    Listen,
    Reset,
    Down,
    Init,
}

public sealed record SessionInfo
{
    public required int Id { get; init; }
    public string? UserName { get; init; }
    public string? Domain { get; init; }
    public string? WinStationName { get; init; }
    public string? ClientName { get; init; }
    public SessionState State { get; init; }
    public DateTime? LogonTime { get; init; }

    /// <summary>Сколько сессия простаивает; null — неизвестно.</summary>
    public TimeSpan? IdleTime { get; init; }

    public bool HasUser => !string.IsNullOrWhiteSpace(UserName);

    /// <summary>CORP\ivanov или просто ivanov, если домен неизвестен.</summary>
    public string AccountName => !HasUser ? string.Empty
        : string.IsNullOrWhiteSpace(Domain) ? UserName! : $"{Domain}\\{UserName}";

    /// <summary>Shadow возможен только к активной сессии с пользователем.</summary>
    public bool CanShadow => HasUser && State == SessionState.Active;

    /// <summary>Почему shadow невозможен; null, если возможен.</summary>
    public string? ShadowBlockedReason
    {
        get
        {
            if (!HasUser) return "В сессии нет пользователя";
            return State switch
            {
                SessionState.Active => null,
                SessionState.Disconnected => "Сессия отключена",
                SessionState.Shadow => "К сессии уже подключён другой администратор",
                SessionState.Connected or SessionState.ConnectQuery or SessionState.Init => "Пользователь ещё не вошёл",
                _ => "Сессия не активна",
            };
        }
    }
}

public enum HostQueryStatus
{
    Ok,
    Unreachable,
    AccessDenied,
    Timeout,
    Error,
}

/// <summary>Итог опроса сессий компьютера. Сервис не бросает исключения для недоступного хоста, а возвращает статус.</summary>
public sealed record SessionListResult(
    HostQueryStatus Status,
    IReadOnlyList<SessionInfo> Sessions,
    string? Message = null,
    DateTime? QueriedAt = null)
{
    public bool IsOk => Status == HostQueryStatus.Ok;

    public static SessionListResult Ok(IReadOnlyList<SessionInfo> sessions) =>
        new(HostQueryStatus.Ok, sessions, null, DateTime.UtcNow);

    public static SessionListResult Failed(HostQueryStatus status, string message) =>
        new(status, Array.Empty<SessionInfo>(), message, DateTime.UtcNow);
}

/// <summary>Где сейчас находится пользователь.</summary>
/// <param name="FromCache">true — данные из кэша, а не свежий опрос.</param>
public sealed record UserLocation(ComputerInfo Computer, SessionInfo Session, DateTime SeenAt, bool FromCache);

/// <summary>Последний известный результат опроса сессий компьютера.</summary>
public sealed record SessionSnapshot(Guid ComputerId, SessionListResult Result, DateTime TakenAt);
