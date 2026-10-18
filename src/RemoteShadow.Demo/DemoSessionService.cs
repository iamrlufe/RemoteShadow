using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>Что сделали с сессией или компьютером в демо-режиме.</summary>
public enum DemoActionKind
{
    SendMessage,
    Disconnect,
    Logoff,
    Reboot,
    Shutdown,
}

/// <summary>Запись журнала демо-действий: что сделали с сессией или компьютером.</summary>
/// <param name="SessionId">Номер сессии; null для перезагрузки и выключения.</param>
/// <param name="Details">Для сообщения — заголовок и текст, для перезагрузки — задержка и пояснение.</param>
public sealed record DemoAction(DateTime TimeUtc, DemoActionKind Kind, string Host, int? SessionId = null, string? Details = null);

/// <summary>
/// Тестовые сессии. Данные берутся из <see cref="DemoData"/>, действия меняют их в памяти.
/// Недоступные хосты отвечают статусом (после паузы), как настоящая реализация; два-три хоста отвечают «нет прав».
/// </summary>
public sealed class DemoSessionService : ISessionService
{
    private readonly DemoData _data;
    private readonly DemoTimings _timings;
    private readonly object _logGate = new();
    private readonly List<DemoAction> _actions = new();

    public DemoSessionService(DemoData data, DemoTimings? timings = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _timings = timings ?? DemoTimings.Default;
    }

    /// <summary>Вызывается после каждого действия (сообщение, отключение, выход, перезагрузка), из потока, который выполнил действие.</summary>
    public event EventHandler<DemoAction>? ActionPerformed;

    /// <summary>Журнал демо-действий с начала работы, старые первыми.</summary>
    public IReadOnlyList<DemoAction> Actions
    {
        get { lock (_logGate) return _actions.ToArray(); }
    }

    /// <summary>Задержка опроса живого хоста: от 150 до 600 мс, зависит только от имени хоста (короткое и полное имя дают одно значение).</summary>
    public TimeSpan GetQueryDelay(string host) =>
        DemoDelay.ByKey(CanonicalName(host), _timings.SessionQueryMin, _timings.SessionQueryMax);

    public async Task<SessionListResult> GetSessionsAsync(string host, CancellationToken ct)
    {
        var computer = _data.FindComputerByHost(host);
        if (computer is null || !_data.IsOnline(computer.Id))
        {
            await DemoDelay.Wait(_timings.UnreachableDelay, ct).ConfigureAwait(false);
            return SessionListResult.Failed(HostQueryStatus.Unreachable, UnreachableMessage(computer?.Name ?? host));
        }

        await DemoDelay.Wait(GetQueryDelay(host), ct).ConfigureAwait(false);

        // Пока ждали, компьютер могли перезагрузить.
        if (!_data.IsOnline(computer.Id))
            return SessionListResult.Failed(HostQueryStatus.Unreachable, UnreachableMessage(computer.Name));
        if (_data.IsAccessDenied(computer.Id))
            return SessionListResult.Failed(HostQueryStatus.AccessDenied, AccessDeniedMessage(computer.Name));

        return SessionListResult.Ok(_data.GetSessions(computer.Id));
    }

    public async Task SendMessageAsync(string host, int sessionId, string title, string text, CancellationToken ct)
    {
        var computer = await ConnectAsync(host, ct).ConfigureAwait(false);
        RequireSession(computer, sessionId);
        Record(DemoActionKind.SendMessage, computer, sessionId, $"{title}: {text}");
    }

    public async Task DisconnectAsync(string host, int sessionId, CancellationToken ct)
    {
        var computer = await ConnectAsync(host, ct).ConfigureAwait(false);
        RequireSession(computer, sessionId);

        _data.UpdateSession(computer.Id, sessionId, s => s with
        {
            State = SessionState.Disconnected,
            WinStationName = null,
            IdleTime = TimeSpan.Zero,
        });
        Record(DemoActionKind.Disconnect, computer, sessionId, null);
    }

    public async Task LogoffAsync(string host, int sessionId, CancellationToken ct)
    {
        var computer = await ConnectAsync(host, ct).ConfigureAwait(false);
        RequireSession(computer, sessionId);

        _data.RemoveSession(computer.Id, sessionId);
        Record(DemoActionKind.Logoff, computer, sessionId, null);
    }

    /// <summary>
    /// Компьютер сразу становится «не в сети» (задержка и сообщение только попадают в журнал) и через
    /// <see cref="DemoTimings.ShutdownReturnDelay"/> (20 секунд) возвращается: после выключения тоже, чтобы демо не пустело.
    /// </summary>
    public async Task ShutdownAsync(string host, bool reboot, TimeSpan delay, string? message, CancellationToken ct)
    {
        var computer = await ConnectAsync(host, ct).ConfigureAwait(false);

        _data.SetOnline(computer.Id, false);
        Record(reboot ? DemoActionKind.Reboot : DemoActionKind.Shutdown, computer, null,
            $"через {(int)delay.TotalSeconds} с" + (string.IsNullOrWhiteSpace(message) ? string.Empty : $": {message}"));

        var id = computer.Id;
        var returnAfter = _timings.ShutdownReturnDelay;
        _ = Task.Run(async () =>
        {
            await DemoDelay.Wait(returnAfter, CancellationToken.None).ConfigureAwait(false);
            _data.SetOnline(id, true);
        }, CancellationToken.None);
    }

    /// <summary>Находит компьютер и «подключается» к нему: недоступный хост — ошибка после паузы, хост без прав — отказ.</summary>
    private async Task<ComputerInfo> ConnectAsync(string host, CancellationToken ct)
    {
        var computer = _data.FindComputerByHost(host)
                       ?? throw new OperationFailedException($"Компьютер «{host}» не найден в домене.", OperationFailure.NotFound);

        if (!_data.IsOnline(computer.Id))
        {
            await DemoDelay.Wait(_timings.UnreachableDelay, ct).ConfigureAwait(false);
            throw new OperationFailedException(UnreachableMessage(computer.Name), OperationFailure.Unreachable);
        }

        await DemoDelay.Wait(_timings.ActionDelay, ct).ConfigureAwait(false);

        if (_data.IsAccessDenied(computer.Id))
            throw new OperationFailedException(AccessDeniedMessage(computer.Name), OperationFailure.AccessDenied);
        if (!_data.IsOnline(computer.Id))
            throw new OperationFailedException(UnreachableMessage(computer.Name), OperationFailure.Unreachable);

        return computer;
    }

    private void RequireSession(ComputerInfo computer, int sessionId)
    {
        if (_data.GetSessions(computer.Id).All(s => s.Id != sessionId))
        {
            throw new OperationFailedException(
                $"Сессия {sessionId} на компьютере «{computer.Name}» не найдена: возможно, пользователь уже вышел.",
                OperationFailure.NotFound);
        }
    }

    private void Record(DemoActionKind kind, ComputerInfo computer, int? sessionId, string? details)
    {
        var action = new DemoAction(DateTime.UtcNow, kind, computer.Name, sessionId, details);
        lock (_logGate) _actions.Add(action);
        ActionPerformed?.Invoke(this, action);
    }

    private string CanonicalName(string host) => _data.FindComputerByHost(host)?.Name ?? host?.Trim() ?? string.Empty;

    private static string UnreachableMessage(string host) =>
        $"Компьютер «{host}» недоступен: не отвечает служба удалённых вызовов. Проверьте, что он включён и порт 445 открыт (1722).";

    private static string AccessDeniedMessage(string host) =>
        $"Нет прав на компьютере «{host}». Нужны права локального администратора (5).";
}
