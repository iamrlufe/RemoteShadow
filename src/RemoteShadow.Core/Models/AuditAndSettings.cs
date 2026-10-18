using RemoteShadow.Core.Actions;

namespace RemoteShadow.Core.Models;

/// <summary>Запись журнала. Пароли и другие секреты сюда не попадают никогда.</summary>
public sealed record AuditRecord
{
    public DateTime TimeUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Кто: CORP\adm-ivanov.</summary>
    public string Account { get; init; } = string.Empty;

    public string Machine { get; init; } = Environment.MachineName;
    public string ActionId { get; init; } = string.Empty;
    public string ActionTitle { get; init; } = string.Empty;

    /// <summary>computer, session, user.</summary>
    public string TargetKind { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public ActionStatus Status { get; init; }
    public string? Message { get; init; }

    /// <summary>Подробности от самого действия (например, «shadow без запроса»).</summary>
    public string? Details { get; init; }
}

public sealed class AppSettings
{
    public bool UseCurrentUser { get; set; } = true;
    public string? LastDomain { get; set; }
    public string? LastDomainController { get; set; }
    public string? LastUserName { get; set; }

    public bool ShowDisabledComputers { get; set; }
    public bool IncludeNestedOu { get; set; } = true;

    /// <summary>Сколько целей обрабатывается одновременно в массовых действиях.</summary>
    public int BulkParallelism { get; set; } = 8;

    /// <summary>Размер пула потоков для блокирующих WTS-вызовов.</summary>
    public int WtsThreads { get; set; } = 32;

    /// <summary>Жёсткий дедлайн на хост при опросе сессий, секунды.</summary>
    public int HostDeadlineSeconds { get; set; } = 10;

    public int OnlineProbeTimeoutMs { get; set; } = 500;
    public int OnlineProbeParallelism { get; set; } = 64;
    public int OnlineCacheSeconds { get; set; } = 120;

    /// <summary>Общая папка для копии журнала (только на дописывание). null — не копировать.</summary>
    public string? AuditCentralFolder { get; set; }

    /// <summary>Вторая, полная загрузка индекса компьютеров, минуты.</summary>
    public int FullRefreshMinutes { get; set; } = 60;

    public int PartialRefreshMinutes { get; set; } = 5;

    public List<Guid> FavoriteComputers { get; set; } = new();
    public List<Guid> RecentComputers { get; set; } = new();

    /// <summary>System, Light или Dark.</summary>
    public string Theme { get; set; } = "System";
}
