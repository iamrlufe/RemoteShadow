using RemoteShadow.Core.Models;

namespace RemoteShadow.Core.Services;

/// <summary>Сессии и управление ими через WTS API.</summary>
public interface ISessionService
{
    /// <summary>
    /// Сессии компьютера. Вызовы WTS блокирующие и не отменяются, поэтому реализация держит отдельный пул потоков
    /// и жёсткий дедлайн на хост. Для недоступного хоста возвращает статус, а не бросает исключение.
    /// </summary>
    Task<SessionListResult> GetSessionsAsync(string host, CancellationToken ct);

    Task SendMessageAsync(string host, int sessionId, string title, string text, CancellationToken ct);
    Task DisconnectAsync(string host, int sessionId, CancellationToken ct);
    Task LogoffAsync(string host, int sessionId, CancellationToken ct);

    /// <summary>Перезагрузка или выключение с предупреждением пользователям и задержкой.</summary>
    Task ShutdownAsync(string host, bool reboot, TimeSpan delay, string? message, CancellationToken ct);
}

/// <summary>Проверка доступности хоста.</summary>
public interface IHostProbe
{
    /// <summary>TCP-подключение к порту с таймаутом. ICMP не используем: он часто закрыт.</summary>
    Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct);

    /// <summary>Онлайн = открыт порт 445.</summary>
    Task<bool> IsOnlineAsync(string host, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Запуск внешних программ (mstsc, ssh, explorer) от имени текущего подключения.</summary>
public interface IProcessLauncher
{
    /// <summary>
    /// Запускает программу со списком аргументов (без cmd /c). В явном режиме — через CreateProcessWithLogonW
    /// с LOGON_NETCREDENTIALS_ONLY. Если не запущена служба seclogon — <see cref="OperationFailedException"/>
    /// с <see cref="OperationFailure.SecondaryLogonUnavailable"/>.
    /// </summary>
    Task StartAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>Полный путь к программе (PATH, System32, WindowsApps) или null, если не найдена.</summary>
    string? FindExecutable(string fileName);
}

/// <summary>Диагностика «почему не работает shadow».</summary>
public interface IDiagnosticsService
{
    Task<DiagnosticReport> RunAsync(ComputerInfo computer, CancellationToken ct);

    /// <summary>Исправляет то, что не управляется GPO (например, запускает TermService). <paramref name="fixId"/> берётся из <see cref="DiagnosticItem.FixId"/>.</summary>
    Task FixAsync(ComputerInfo computer, string fixId, CancellationToken ct);
}
