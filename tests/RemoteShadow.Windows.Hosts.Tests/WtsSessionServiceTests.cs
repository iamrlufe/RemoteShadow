using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts.Tests;

public class WtsSessionServiceTests
{
    private sealed class FakeWtsApi : IWtsApi
    {
        public Func<string, IReadOnlyList<SessionInfo>> Query { get; set; } = _ => [];
        public Action<string, int, string, string>? OnSendMessage { get; set; }
        public Action<string, int>? OnDisconnect { get; set; }
        public Action<string, int>? OnLogoff { get; set; }
        public Action<string, bool, uint, string?>? OnShutdown { get; set; }

        public IReadOnlyList<SessionInfo> QuerySessions(string host) => Query(host);
        public void SendMessage(string host, int sessionId, string title, string text) => OnSendMessage?.Invoke(host, sessionId, title, text);
        public void Disconnect(string host, int sessionId) => OnDisconnect?.Invoke(host, sessionId);
        public void Logoff(string host, int sessionId) => OnLogoff?.Invoke(host, sessionId);
        public void Shutdown(string host, bool reboot, uint timeoutSeconds, string? message) => OnShutdown?.Invoke(host, reboot, timeoutSeconds, message);
    }

    private static (WtsSessionService Service, FakeConnection Connection, FakeSettingsStore Settings, FakeWtsApi Api) Create(
        int threads = 4, int deadlineSeconds = 5)
    {
        var connection = new FakeConnection();
        var settings = new FakeSettingsStore { Settings = new AppSettings { WtsThreads = threads, HostDeadlineSeconds = deadlineSeconds } };
        var api = new FakeWtsApi();
        var service = new WtsSessionService(TestConnections.LoggedIn(connection), settings, api);
        return (service, connection, settings, api);
    }

    private static SessionInfo Session(int id) => new() { Id = id, UserName = "ivanov", Domain = "CORP", State = SessionState.Active };

    [Fact]
    public async Task GetSessions_returns_sessions_and_runs_impersonated()
    {
        var (service, connection, _, api) = Create();
        api.Query = host => host == "pc1.corp.local" ? [Session(1), Session(2)] : [];

        using (service)
        {
            var result = await service.GetSessionsAsync("pc1.corp.local", CancellationToken.None);

            Assert.True(result.IsOk);
            Assert.Equal([1, 2], result.Sessions.Select(s => s.Id));
            Assert.NotNull(result.QueriedAt);
            Assert.Equal(1, connection.ImpersonationCount);
        }
    }

    [Theory]
    [InlineData(1722, HostQueryStatus.Unreachable)]
    [InlineData(53, HostQueryStatus.Unreachable)]
    [InlineData(5, HostQueryStatus.AccessDenied)]
    [InlineData(1460, HostQueryStatus.Timeout)]
    [InlineData(87, HostQueryStatus.Error)]
    public async Task GetSessions_maps_win32_errors_to_status_without_throwing(int code, HostQueryStatus expected)
    {
        var (service, _, _, api) = Create();
        api.Query = _ => throw new WtsException("WTSEnumerateSessionsExW", code);

        using (service)
        {
            var result = await service.GetSessionsAsync("pc1", CancellationToken.None);

            Assert.Equal(expected, result.Status);
            Assert.Empty(result.Sessions);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.Contains("pc1", result.Message);
        }
    }

    [Fact]
    public async Task GetSessions_unexpected_exception_becomes_error_status()
    {
        var (service, _, _, api) = Create();
        api.Query = _ => throw new InvalidOperationException("странный сбой");

        using (service)
        {
            var result = await service.GetSessionsAsync("pc1", CancellationToken.None);

            Assert.Equal(HostQueryStatus.Error, result.Status);
            Assert.Contains("странный сбой", result.Message);
        }
    }

    [Fact]
    public async Task GetSessions_failed_impersonation_uses_failure_kind()
    {
        var (service, _, _, api) = Create();
        api.Query = _ => throw new OperationFailedException("Учётные данные отклонены.", OperationFailure.AccessDenied);

        using (service)
        {
            var result = await service.GetSessionsAsync("pc1", CancellationToken.None);

            Assert.Equal(HostQueryStatus.AccessDenied, result.Status);
            Assert.Equal("Учётные данные отклонены.", result.Message);
        }
    }

    [Fact]
    public async Task GetSessions_hung_host_returns_timeout_at_deadline_while_thread_still_works()
    {
        var (service, _, _, api) = Create(threads: 2, deadlineSeconds: 1);
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        api.Query = _ =>
        {
            release.Wait();
            finished.Set();
            return [];
        };

        using (service)
        {
            var started = DateTime.UtcNow;
            var result = await service.GetSessionsAsync("hung.corp.local", CancellationToken.None);
            var elapsed = DateTime.UtcNow - started;

            Assert.Equal(HostQueryStatus.Timeout, result.Status);
            Assert.Contains("hung.corp.local", result.Message);
            Assert.Contains("не отвечает", result.Message);
            Assert.InRange(elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10));
            Assert.False(finished.IsSet);

            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task GetSessions_cancellation_throws_operation_canceled()
    {
        var (service, _, _, api) = Create();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        api.Query = _ =>
        {
            started.Set();
            release.Wait();
            return [];
        };

        using (service)
        {
            var task = service.GetSessionsAsync("pc1", cts.Token);
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            release.Set();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-evil")]
    [InlineData("/server")]
    [InlineData("pc 1")]
    public async Task Invalid_host_name_is_rejected_before_any_call(string host)
    {
        var (service, connection, settings, api) = Create();
        var called = false;
        api.Query = _ =>
        {
            called = true;
            return [];
        };
        api.OnDisconnect = (_, _) => called = true;

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.GetSessionsAsync(host, CancellationToken.None));
            Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
            await Assert.ThrowsAsync<OperationFailedException>(() => service.DisconnectAsync(host, 1, CancellationToken.None));
            await Assert.ThrowsAsync<OperationFailedException>(() => service.LogoffAsync(host, 1, CancellationToken.None));
            await Assert.ThrowsAsync<OperationFailedException>(() => service.SendMessageAsync(host, 1, "t", "x", CancellationToken.None));
            await Assert.ThrowsAsync<OperationFailedException>(() => service.ShutdownAsync(host, true, TimeSpan.Zero, null, CancellationToken.None));
        }

        Assert.False(called);
        Assert.Equal(0, connection.ImpersonationCount);
        Assert.Equal(0, settings.LoadCount);
    }

    [Fact]
    public async Task Not_logged_in_throws_not_logged_in()
    {
        var service = new WtsSessionService(TestConnections.LoggedOut(), new FakeSettingsStore(), new FakeWtsApi());

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.GetSessionsAsync("pc1", CancellationToken.None));
            Assert.Equal(OperationFailure.NotLoggedIn, ex.Failure);
            ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.LogoffAsync("pc1", 1, CancellationToken.None));
            Assert.Equal(OperationFailure.NotLoggedIn, ex.Failure);
        }
    }

    [Fact]
    public async Task Settings_are_read_once_on_first_use_and_limit_parallelism()
    {
        var (service, _, settings, api) = Create(threads: 2, deadlineSeconds: 20);
        var current = 0;
        var max = 0;
        api.Query = _ =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            do
            {
                seen = Volatile.Read(ref max);
            }
            while (now > seen && Interlocked.CompareExchange(ref max, now, seen) != seen);

            Thread.Sleep(40);
            Interlocked.Decrement(ref current);
            return [];
        };

        using (service)
        {
            Assert.Equal(0, settings.LoadCount);

            var results = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(i => service.GetSessionsAsync($"pc{i}", CancellationToken.None)));

            Assert.All(results, r => Assert.True(r.IsOk));
            Assert.Equal(1, settings.LoadCount);
            Assert.InRange(max, 1, 2);
        }
    }

    [Fact]
    public async Task Settings_failure_falls_back_to_defaults()
    {
        var api = new FakeWtsApi { Query = _ => [Session(1)] };
        var service = new WtsSessionService(TestConnections.LoggedIn(new FakeConnection()), new ThrowingSettings(), api);

        using (service)
        {
            var result = await service.GetSessionsAsync("pc1", CancellationToken.None);

            Assert.True(result.IsOk);
        }
    }

    private sealed class ThrowingSettings : RemoteShadow.Core.Services.ISettingsStore
    {
        public AppSettings Load() => throw new IOException("нет файла");
        public void Save(AppSettings settings) { }
    }

    [Fact]
    public async Task SendMessage_normalizes_title_and_text()
    {
        var (service, connection, _, api) = Create();
        string? sentTitle = null, sentText = null;
        var sentSession = -1;
        api.OnSendMessage = (_, id, title, text) =>
        {
            sentSession = id;
            sentTitle = title;
            sentText = text;
        };

        using (service)
        {
            await service.SendMessageAsync("pc1", 3, "  ", new string('я', 5000), CancellationToken.None);
        }

        Assert.Equal(3, sentSession);
        Assert.Equal(WtsConversions.DefaultMessageTitle, sentTitle);
        Assert.Equal(WtsConversions.MaxMessageTextChars, sentText!.Length);
        Assert.Equal(1, connection.ImpersonationCount);
    }

    [Fact]
    public async Task SendMessage_empty_text_is_invalid_input()
    {
        var (service, _, _, _) = Create();

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(
                () => service.SendMessageAsync("pc1", 1, "Заголовок", "  ", CancellationToken.None));
            Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        }
    }

    [Fact]
    public async Task Negative_session_id_is_invalid_input()
    {
        var (service, _, _, _) = Create();

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.DisconnectAsync("pc1", -1, CancellationToken.None));
            Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        }
    }

    [Fact]
    public async Task Disconnect_and_logoff_call_api_with_session_id()
    {
        var (service, connection, _, api) = Create();
        var calls = new List<string>();
        api.OnDisconnect = (host, id) => calls.Add($"disconnect {host} {id}");
        api.OnLogoff = (host, id) => calls.Add($"logoff {host} {id}");

        using (service)
        {
            await service.DisconnectAsync("pc1", 2, CancellationToken.None);
            await service.LogoffAsync("pc1", 5, CancellationToken.None);
        }

        Assert.Equal(["disconnect pc1 2", "logoff pc1 5"], calls);
        Assert.Equal(2, connection.ImpersonationCount);
    }

    [Fact]
    public async Task Operation_error_becomes_operation_failed_with_russian_text_and_kind()
    {
        var (service, _, _, api) = Create();
        api.OnLogoff = (_, _) => throw new WtsException("WTSLogoffSession", 1722);

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.LogoffAsync("pc1", 2, CancellationToken.None));

            Assert.Equal(OperationFailure.Unreachable, ex.Failure);
            Assert.StartsWith("Не удалось завершить сессию 2 на компьютере pc1:", ex.Message);
            Assert.EndsWith("(код 1722).", ex.Message);
            Assert.IsType<WtsException>(ex.InnerException);
        }
    }

    [Fact]
    public async Task Operation_access_denied_has_admin_rights_text()
    {
        var (service, _, _, api) = Create();
        api.OnDisconnect = (_, _) => throw new WtsException("WTSDisconnectSession", 5);

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.DisconnectAsync("pc1", 2, CancellationToken.None));

            Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
            Assert.Contains("нет прав администратора", ex.Message);
        }
    }

    [Fact]
    public async Task Operation_timeout_warns_that_the_command_may_still_run()
    {
        var (service, _, _, api) = Create(deadlineSeconds: 1);
        using var release = new ManualResetEventSlim();
        api.OnDisconnect = (_, _) => release.Wait();

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.DisconnectAsync("pc1", 2, CancellationToken.None));
            release.Set();

            Assert.Equal(OperationFailure.Timeout, ex.Failure);
            Assert.Contains("отключить сессию 2", ex.Message);
            Assert.Contains("могла быть выполнена", ex.Message);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shutdown_passes_flags_delay_and_message(bool reboot)
    {
        var (service, connection, _, api) = Create();
        string? capturedHost = null;
        bool? capturedReboot = null;
        uint capturedSeconds = 0;
        string? capturedMessage = null;
        api.OnShutdown = (host, r, seconds, message) =>
        {
            capturedHost = host;
            capturedReboot = r;
            capturedSeconds = seconds;
            capturedMessage = message;
        };

        using (service)
        {
            await service.ShutdownAsync("pc1", reboot, TimeSpan.FromMinutes(5), " Сохраните работу ", CancellationToken.None);
        }

        Assert.Equal("pc1", capturedHost);
        Assert.Equal(reboot, capturedReboot);
        Assert.Equal(300u, capturedSeconds);
        Assert.Equal("Сохраните работу", capturedMessage);
        Assert.Equal(1, connection.ImpersonationCount);
    }

    [Fact]
    public async Task Shutdown_empty_message_is_passed_as_null()
    {
        var (service, _, _, api) = Create();
        var message = "sentinel";
        api.OnShutdown = (_, _, _, m) => message = m;

        using (service)
        {
            await service.ShutdownAsync("pc1", true, TimeSpan.Zero, "  ", CancellationToken.None);
        }

        Assert.Null(message);
    }

    [Fact]
    public async Task Shutdown_access_denied_explains_remote_shutdown_privilege()
    {
        var (service, _, _, api) = Create();
        api.OnShutdown = (_, _, _, _) => throw new WtsException("InitiateSystemShutdownExW", 5);

        using (service)
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(
                () => service.ShutdownAsync("pc1", true, TimeSpan.FromSeconds(30), "msg", CancellationToken.None));

            Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
            Assert.Contains("SeRemoteShutdownPrivilege", ex.Message);
            Assert.StartsWith("Не удалось перезагрузить на компьютере pc1", ex.Message);
        }
    }

    [Fact]
    public void Constructing_the_service_does_not_read_settings()
    {
        // Только создание: пул и чтение настроек откладываются до первого вызова.
        var settings = new FakeSettingsStore();
        using var service = new WtsSessionService(TestConnections.LoggedOut(), settings);

        Assert.Equal(0, settings.LoadCount);
    }
}
