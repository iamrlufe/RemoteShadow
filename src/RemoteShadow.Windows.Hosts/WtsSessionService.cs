using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows.Hosts;

/// <summary>
/// Сессии пользователей и управление ими через WTS API. Все вызовы блокирующие, поэтому идут через собственный
/// пул потоков (<see cref="DedicatedThreadPool"/>) с жёстким дедлайном на хост и от имени текущего подключения.
/// Размер пула и дедлайн берутся из настроек при первом обращении.
/// </summary>
public sealed class WtsSessionService : ISessionService, IDisposable
{
    private const int DefaultThreads = 32;
    private const int MaxThreads = 256;
    private const int DefaultDeadlineSeconds = 10;
    private const int MaxDeadlineSeconds = 300;
    private const int QueueSlotsPerThread = 32;

    private readonly ICurrentConnection _connection;
    private readonly ISettingsStore _settings;
    private readonly IWtsApi _api;
    private readonly object _gate = new();
    private DedicatedThreadPool? _pool;
    private TimeSpan _deadline;

    public WtsSessionService(ICurrentConnection connection, ISettingsStore settings)
        : this(connection, settings, new WtsApi())
    {
    }

    internal WtsSessionService(ICurrentConnection connection, ISettingsStore settings, IWtsApi api)
    {
        _connection = connection;
        _settings = settings;
        _api = api;
    }

    public async Task<SessionListResult> GetSessionsAsync(string host, CancellationToken ct)
    {
        HostNameValidator.Require(host);
        var connection = _connection.Require();
        var (pool, deadline) = GetPool();

        try
        {
            var sessions = await pool
                .RunAsync(() => connection.RunImpersonated(() => _api.QuerySessions(host)), deadline, ct)
                .ConfigureAwait(false);
            return SessionListResult.Ok(sessions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return SessionListResult.Failed(HostQueryStatus.Timeout, TimeoutText(host, deadline));
        }
        catch (WtsException ex)
        {
            var failure = WtsErrorMapper.MapQuery(ex.ErrorCode, host);
            return SessionListResult.Failed(failure.Status, failure.Message);
        }
        catch (OperationFailedException ex)
        {
            return SessionListResult.Failed(ToStatus(ex.Failure), ex.Message);
        }
        catch (Exception ex)
        {
            return SessionListResult.Failed(HostQueryStatus.Error, $"Не удалось получить сессии компьютера {host}: {ex.Message}");
        }
    }

    public async Task SendMessageAsync(string host, int sessionId, string title, string text, CancellationToken ct)
    {
        HostNameValidator.Require(host);
        RequireSessionId(sessionId);
        var normalizedTitle = WtsConversions.NormalizeMessageTitle(title);
        var normalizedText = WtsConversions.NormalizeMessageText(text);

        await RunActionAsync(
            host,
            $"отправить сообщение в сессию {sessionId}",
            () => _api.SendMessage(host, sessionId, normalizedTitle, normalizedText),
            WtsErrorMapper.MapOperation,
            ct);
    }

    public async Task DisconnectAsync(string host, int sessionId, CancellationToken ct)
    {
        HostNameValidator.Require(host);
        RequireSessionId(sessionId);

        await RunActionAsync(
            host,
            $"отключить сессию {sessionId}",
            () => _api.Disconnect(host, sessionId),
            WtsErrorMapper.MapOperation,
            ct);
    }

    public async Task LogoffAsync(string host, int sessionId, CancellationToken ct)
    {
        HostNameValidator.Require(host);
        RequireSessionId(sessionId);

        await RunActionAsync(
            host,
            $"завершить сессию {sessionId}",
            () => _api.Logoff(host, sessionId),
            WtsErrorMapper.MapOperation,
            ct);
    }

    public async Task ShutdownAsync(string host, bool reboot, TimeSpan delay, string? message, CancellationToken ct)
    {
        HostNameValidator.Require(host);
        var seconds = WtsConversions.ShutdownTimeoutSeconds(delay);
        var text = WtsConversions.NormalizeShutdownMessage(message);

        await RunActionAsync(
            host,
            reboot ? "перезагрузить" : "выключить",
            () => _api.Shutdown(host, reboot, seconds, text),
            WtsErrorMapper.MapShutdown,
            ct);
    }

    public void Dispose()
    {
        DedicatedThreadPool? pool;
        lock (_gate)
        {
            pool = _pool;
            _pool = null;
        }

        pool?.Dispose();
    }

    private async Task RunActionAsync(
        string host,
        string operation,
        Action work,
        Func<string, int, string, WtsFailure> mapError,
        CancellationToken ct)
    {
        var connection = _connection.Require();
        var (pool, deadline) = GetPool();

        try
        {
            await pool.RunAsync(() => connection.RunImpersonated(work), deadline, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Вызов мог дойти до компьютера и выполниться позже: поток пула его не прерывает.
            throw new OperationFailedException(
                $"Не удалось {operation} на компьютере {host}: нет ответа за {deadline.TotalSeconds:0} с. " +
                "Команда могла быть выполнена — проверьте состояние компьютера.",
                OperationFailure.Timeout,
                ex);
        }
        catch (WtsException ex)
        {
            var failure = mapError(operation, ex.ErrorCode, host);
            throw new OperationFailedException(failure.Message, failure.Failure, ex);
        }
    }

    private (DedicatedThreadPool Pool, TimeSpan Deadline) GetPool()
    {
        lock (_gate)
        {
            if (_pool is null)
            {
                var settings = LoadSettings();
                var threads = settings.WtsThreads <= 0 ? DefaultThreads : Math.Min(settings.WtsThreads, MaxThreads);
                var seconds = settings.HostDeadlineSeconds <= 0
                    ? DefaultDeadlineSeconds
                    : Math.Min(settings.HostDeadlineSeconds, MaxDeadlineSeconds);

                _pool = new DedicatedThreadPool(threads, threads * QueueSlotsPerThread);
                _deadline = TimeSpan.FromSeconds(seconds);
            }

            return (_pool, _deadline);
        }
    }

    private AppSettings LoadSettings()
    {
        try
        {
            return _settings.Load();
        }
        catch (Exception)
        {
            // Настройки только подстраивают пул: сбой чтения не должен ломать опрос сессий.
            return new AppSettings();
        }
    }

    private static void RequireSessionId(int sessionId)
    {
        if (sessionId < 0)
            throw new OperationFailedException($"Недопустимый номер сессии: {sessionId}.", OperationFailure.InvalidInput);
    }

    private static string TimeoutText(string host, TimeSpan deadline) =>
        $"Компьютер {host} не отвечает: нет ответа за {deadline.TotalSeconds:0} с.";

    private static HostQueryStatus ToStatus(OperationFailure failure) => failure switch
    {
        OperationFailure.Unreachable => HostQueryStatus.Unreachable,
        OperationFailure.AccessDenied => HostQueryStatus.AccessDenied,
        OperationFailure.Timeout => HostQueryStatus.Timeout,
        _ => HostQueryStatus.Error,
    };
}
