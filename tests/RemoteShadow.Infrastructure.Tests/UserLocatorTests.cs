using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

public class UserLocatorTests
{
    private const string AccountingGroupOu = "OU=Группа 1," + AccountingOu;

    private sealed class Fixture
    {
        private readonly Dictionary<string, IReadOnlyList<SessionInfo>> _sessionsByHost = new(StringComparer.OrdinalIgnoreCase);
        private int _next;

        public Fixture()
        {
            Sessions.Respond = host => SessionListResult.Ok(
                _sessionsByHost.TryGetValue(host, out var sessions) ? sessions : Array.Empty<SessionInfo>());
            Locator = new UserLocator(Index, Sessions, Cache, Users, Probe, Settings, Time);
        }

        public FakeComputerIndex Index { get; } = new();
        public FakeSessionService Sessions { get; } = new();
        public SessionCache Cache { get; } = new();
        public FakeUserDirectory Users { get; } = new();
        public FakeHostProbe Probe { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public UserLocator Locator { get; }

        /// <summary>Добавляет компьютер в индекс; <paramref name="sessions"/> — то, что вернёт опрос его сессий.</summary>
        public ComputerInfo AddPc(
            string name,
            IReadOnlyList<SessionInfo>? sessions = null,
            string ou = AccountingOu,
            bool enabled = true,
            bool? online = null)
        {
            var pc = Pc(name, ou, id: ++_next, enabled: enabled, online: online);
            Index.Add(pc);
            _sessionsByHost[pc.HostName] = sessions ?? Array.Empty<SessionInfo>();
            return pc;
        }

        public void SetCached(ComputerInfo pc, DateTime takenAt, params SessionInfo[] sessions) =>
            Cache.Set(new SessionSnapshot(pc.Id, SessionListResult.Ok(sessions), takenAt));

        public Task<List<UserLocation>> FindAsync(string text, string? scope = null, bool nested = true, CancellationToken ct = default) =>
            CollectAsync(Locator.FindAsync(text, scope, nested, ct));
    }

    private static async Task<List<UserLocation>> CollectAsync(IAsyncEnumerable<UserLocation> source)
    {
        var list = new List<UserLocation>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    private static DateTime At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, DateTimeKind.Utc);

    private static string[] Summary(IEnumerable<UserLocation> found) =>
        found.Select(l => $"{l.Computer.Name}/{l.Session.UserName}/{(l.FromCache ? "cache" : "live")}").ToArray();

    // ----- FindCached ----------------------------------------------------------------------------------------------------

    [Fact]
    public void FindCached_matches_login_ignoring_case_and_skips_sessions_without_user_and_unknown_computers()
    {
        var fx = new Fixture();
        var pc1 = fx.AddPc("PC-1");
        var pc2 = fx.AddPc("PC-2");
        var unknown = Pc("GONE", id: 99);
        fx.SetCached(pc1, At(10), Session(0, null, SessionState.Disconnected), Session(1, "ivanov"));
        fx.SetCached(pc2, At(10, 5), Session(1, "petrov"), Session(2, "ivanova"));
        fx.SetCached(unknown, At(11), Session(1, "ivanov"));

        var found = fx.Locator.FindCached("IVAN");

        Assert.Equal(new[] { "PC-2/ivanova/cache", "PC-1/ivanov/cache" }, Summary(found));
        Assert.Equal(new[] { At(10, 5), At(10) }, found.Select(l => l.SeenAt));
    }

    [Fact]
    public void FindCached_matches_domain_qualified_account_name()
    {
        var fx = new Fixture();
        var pc1 = fx.AddPc("PC-1");
        var pc2 = fx.AddPc("PC-2");
        fx.SetCached(pc1, At(10), Session(1, "ivanov", domain: "CORP"));
        fx.SetCached(pc2, At(10), Session(1, "ivanov", domain: "OTHER"));

        var found = fx.Locator.FindCached(@"corp\ivanov");

        Assert.Equal(new[] { "PC-1/ivanov/cache" }, Summary(found));
    }

    [Fact]
    public void FindCached_returns_nothing_for_empty_text_and_empty_cache()
    {
        var fx = new Fixture();
        fx.SetCached(fx.AddPc("PC-1"), At(10), Session(1, "ivanov"));

        Assert.Empty(fx.Locator.FindCached(""));
        Assert.Empty(fx.Locator.FindCached("   "));
        Assert.Empty(new Fixture().Locator.FindCached("ivanov"));
    }

    [Fact]
    public void FindCached_does_not_touch_the_network()
    {
        var fx = new Fixture();
        fx.SetCached(fx.AddPc("PC-1"), At(10), Session(1, "ivanov"));

        fx.Locator.FindCached("ivanov");

        Assert.Empty(fx.Probe.Calls);
        Assert.Empty(fx.Sessions.Calls);
        Assert.Empty(fx.Users.SearchTexts);
    }

    [Fact]
    public async Task FindCached_matches_full_name_once_it_is_known_from_a_previous_search()
    {
        var fx = new Fixture();
        var pc = fx.AddPc("PC-1", [Session(1, "petrova")]);
        fx.SetCached(pc, At(10), Session(1, "petrova"));
        Assert.Empty(fx.Locator.FindCached("Анна")); // пока ФИО неизвестно

        fx.Users.OnSearch = _ => new[] { User("petrova", "Петрова Анна") };
        await fx.FindAsync("Петрова");

        Assert.Equal(new[] { "PC-1/petrova/cache" }, Summary(fx.Locator.FindCached("анна")));
        Assert.Equal(new[] { "PC-1/petrova/cache" }, Summary(fx.Locator.FindCached("ПЕТРОВА АННА")));
        Assert.Empty(fx.Locator.FindCached("Сидоров"));
    }

    [Fact]
    public async Task Known_full_name_does_not_match_other_users_sessions()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "petrova")]);
        var pc2 = fx.AddPc("PC-2", [Session(1, "sidorov")]);
        fx.SetCached(pc2, At(10), Session(1, "sidorov"));
        fx.Users.OnSearch = _ => new[] { User("petrova", "Петрова Анна") };
        await fx.FindAsync("Петрова");

        Assert.DoesNotContain(fx.Locator.FindCached("Анна"), l => l.Computer.Id == pc2.Id);
        Assert.Equal(new[] { "PC-1/petrova/cache" }, Summary(fx.Locator.FindCached("анна")));
    }

    // ----- FindAsync: кэш, потоковая выдача -------------------------------------------------------------------------------

    [Fact]
    public async Task FindAsync_gives_cached_matches_first_then_live_ones_as_they_arrive()
    {
        var fx = new Fixture();
        var stale = fx.AddPc("PC-1"); // в кэше пользователь есть, а сейчас там пусто
        fx.AddPc("PC-2", [Session(1, "ivanov")]);
        var slow = fx.AddPc("PC-3", [Session(2, "ivanov")]);
        fx.SetCached(stale, At(9), Session(1, "ivanov"));
        var gate = new Gate();
        fx.Sessions.Hold = (host, ct) => host == slow.HostName ? gate.WaitAsync(ct) : Task.CompletedTask;

        await using var enumerator = fx.Locator.FindAsync("ivanov", null, true, default).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WithTimeout());
        Assert.Equal("PC-1/ivanov/cache", Summary(new[] { enumerator.Current }).Single());

        // PC-2 ответил — находка приходит сразу, хотя PC-3 ещё отвечает.
        Assert.True(await enumerator.MoveNextAsync().AsTask().WithTimeout());
        Assert.Equal("PC-2/ivanov/live", Summary(new[] { enumerator.Current }).Single());
        Assert.Equal(1, enumerator.Current.Session.Id);
        Assert.Equal(At(10), enumerator.Current.SeenAt); // время из TimeProvider
        Assert.False(gate.IsOpen);

        var third = enumerator.MoveNextAsync().AsTask();
        await Wait.HoldsAsync(() => !third.IsCompleted, 100, "PC-3 ещё не ответил");
        gate.Open();
        Assert.True(await third.WithTimeout());
        Assert.Equal("PC-3/ivanov/live", Summary(new[] { enumerator.Current }).Single());

        Assert.False(await enumerator.MoveNextAsync().AsTask().WithTimeout());
    }

    [Fact]
    public async Task Same_session_found_in_cache_and_live_is_returned_twice_with_different_flag()
    {
        var fx = new Fixture();
        var pc = fx.AddPc("PC-1", [Session(1, "ivanov")]);
        fx.SetCached(pc, At(9), Session(1, "ivanov"));

        var found = await fx.FindAsync("ivanov");

        Assert.Equal(new[] { "PC-1/ivanov/cache", "PC-1/ivanov/live" }, Summary(found));
        Assert.Equal(At(9), found[0].SeenAt);
        Assert.Equal(At(10), found[1].SeenAt);
    }

    [Fact]
    public async Task Live_results_are_stored_in_session_cache_including_sessions_of_other_users()
    {
        var fx = new Fixture();
        var pc1 = fx.AddPc("PC-1", [Session(1, "ivanov"), Session(2, "petrov")]);
        var pc2 = fx.AddPc("PC-2", [Session(1, "sidorov")]);

        await fx.FindAsync("ivanov");

        var snapshot1 = fx.Cache.Get(pc1.Id);
        Assert.NotNull(snapshot1);
        Assert.Equal(2, snapshot1!.Result.Sessions.Count);
        Assert.Equal(At(10), snapshot1.TakenAt);
        Assert.Single(fx.Cache.Get(pc2.Id)!.Result.Sessions);
        // Следующий поиск по кэшу, без сети, видит и чужие сессии.
        Assert.Equal(new[] { "PC-2/sidorov/cache" }, Summary(fx.Locator.FindCached("sidorov")));
    }

    [Fact]
    public async Task Failed_poll_does_not_overwrite_earlier_good_data_but_is_stored_when_nothing_was_known()
    {
        var fx = new Fixture();
        var known = fx.AddPc("PC-1");
        var unknown = fx.AddPc("PC-2");
        fx.SetCached(known, At(9), Session(1, "ivanov"));
        var good = fx.Cache.Get(known.Id);
        fx.Sessions.Respond = host => SessionListResult.Failed(HostQueryStatus.Timeout, "нет ответа");

        var found = await fx.FindAsync("ivanov");

        Assert.Equal(new[] { "PC-1/ivanov/cache" }, Summary(found));
        Assert.Same(good, fx.Cache.Get(known.Id));
        Assert.Equal(HostQueryStatus.Timeout, fx.Cache.Get(unknown.Id)!.Result.Status);
    }

    [Fact]
    public async Task Sessions_without_a_user_are_never_returned()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(0, null, SessionState.Listen), Session(65536, "", SessionState.Listen), Session(1, "ivanov")]);

        var found = await fx.FindAsync("ivan");

        Assert.Equal(new[] { "PC-1/ivanov/live" }, Summary(found));
    }

    [Fact]
    public async Task Empty_text_returns_nothing_and_asks_nobody()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "ivanov")]);

        Assert.Empty(await fx.FindAsync(""));
        Assert.Empty(await fx.FindAsync("   "));

        Assert.Empty(fx.Users.SearchTexts);
        Assert.Empty(fx.Probe.Calls);
    }

    // ----- разрешение текста в логины -------------------------------------------------------------------------------------

    [Fact]
    public async Task Surname_is_resolved_to_logins_through_the_user_directory()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "bob")]);
        var target = fx.AddPc("PC-2", [Session(1, "avi")]);
        fx.AddPc("PC-3", [Session(1, "IVANOV-ADM")]); // не тот: ни логин, ни ФИО не подходят
        fx.Users.OnSearch = text => text == "Иванов"
            ? new[] { User("avi", "Иванов Алексей"), User("", "Без логина") }
            : Array.Empty<UserInfo>();

        var found = await fx.FindAsync("  Иванов  ");

        Assert.Equal(new[] { "Иванов" }, fx.Users.SearchTexts); // текст обрезан
        Assert.Equal(new[] { "PC-2/avi/live" }, Summary(found));
        Assert.Equal(target.Id, found[0].Computer.Id);
    }

    [Fact]
    public async Task Resolved_logins_match_exactly_ignoring_case_and_text_also_matches_as_login_substring()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "AVI")]);
        fx.AddPc("PC-2", [Session(1, "avi2")]); // «avi2» не равен «avi» и не содержит «Иванов»
        fx.AddPc("PC-3", [Session(1, "Иванов-и")]); // содержит исходный текст
        fx.Users.OnSearch = _ => new[] { User("avi", "Иванов Алексей") };

        var found = await fx.FindAsync("Иванов");

        Assert.Equal(new[] { "PC-1/AVI/live", "PC-3/Иванов-и/live" }, Summary(found.OrderBy(l => l.Computer.Name)));
    }

    [Fact]
    public async Task User_directory_failure_is_not_fatal_and_text_is_used_as_login()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "ivanov")]);
        fx.AddPc("PC-2", [Session(1, "petrov")]);
        fx.Users.SearchFails = new OperationFailedException("Нет доступа к каталогу пользователей.", OperationFailure.AccessDenied);

        var found = await fx.FindAsync("ivan");

        Assert.Equal(new[] { "PC-1/ivanov/live" }, Summary(found));
    }

    [Fact]
    public async Task Spurious_cancellation_exception_from_directory_is_not_fatal_either()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "ivanov")]);
        fx.Users.SearchFails = new OperationCanceledException("таймаут запроса");

        var found = await fx.FindAsync("ivanov");

        Assert.Single(found);
    }

    [Fact]
    public async Task Cancelled_token_during_user_search_stops_the_search()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "ivanov")]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        fx.Users.SearchFails = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fx.FindAsync("ivanov", ct: cts.Token));

        Assert.Empty(fx.Probe.Calls);
    }

    // ----- кого опрашивать ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scope_limits_computers_direct_nested_or_whole_domain_and_disabled_are_skipped()
    {
        var fx = new Fixture();
        var a1 = fx.AddPc("A-1", ou: AccountingOu);
        var a2 = fx.AddPc("A-2", ou: AccountingOu);
        var g1 = fx.AddPc("G-1", ou: AccountingGroupOu);
        var w1 = fx.AddPc("W-1", ou: WarehouseOu);
        fx.AddPc("D-1", ou: AccountingOu, enabled: false);

        async Task<string[]> ProbedAsync(string? scope, bool nested)
        {
            while (fx.Probe.Calls.TryDequeue(out _))
            {
            }

            await fx.FindAsync("ivanov", scope, nested);
            return fx.Probe.Calls.Order().ToArray();
        }

        Assert.Equal(new[] { a1.HostName, a2.HostName }, await ProbedAsync(AccountingOu, false));
        Assert.Equal(new[] { a1.HostName, a2.HostName, g1.HostName }, await ProbedAsync(AccountingOu, true));
        Assert.Equal(new[] { a1.HostName, a2.HostName, g1.HostName, w1.HostName }, await ProbedAsync(null, true));
        Assert.Equal(new[] { a1.HostName, a2.HostName, g1.HostName, w1.HostName }, await ProbedAsync(null, false)); // флаг вложенности без области не нужен
        Assert.Empty(await ProbedAsync("OU=Нет такого,DC=corp,DC=local", true));
    }

    [Fact]
    public async Task Sessions_are_requested_only_from_computers_that_answered_the_port_check()
    {
        var fx = new Fixture();
        var up = fx.AddPc("UP-1", [Session(1, "ivanov")]);
        var down = fx.AddPc("DOWN-1", [Session(1, "ivanov")]);
        fx.Probe.Online = host => host != down.HostName;

        var found = await fx.FindAsync("ivanov");

        Assert.Equal(new[] { "UP-1/ivanov/live" }, Summary(found));
        Assert.Equal(new[] { up.HostName }, fx.Sessions.Calls);
        Assert.Contains(down.HostName, fx.Probe.Calls);
        Assert.Null(fx.Cache.Get(down.Id));
    }

    [Fact]
    public async Task Port_check_uses_the_quick_timeout_from_settings()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1");
        fx.AddPc("PC-2");

        await fx.FindAsync("ivanov");
        fx.Settings.Settings.OnlineProbeTimeoutMs = 300;
        await fx.FindAsync("ivanov");

        Assert.Equal(
            new[] { 500d, 500d, 300d, 300d },
            fx.Probe.Timeouts.Select(t => t.TotalMilliseconds));
    }

    [Fact]
    public async Task Computers_known_to_be_online_are_checked_first()
    {
        var fx = new Fixture();
        fx.Settings.Settings.OnlineProbeParallelism = 1;
        var unknown = fx.AddPc("X-1");
        var online1 = fx.AddPc("X-2", online: true);
        var offline = fx.AddPc("X-3", online: false);
        var online2 = fx.AddPc("X-4", online: true);

        await fx.FindAsync("ivanov");

        Assert.Equal(
            new[] { online1.HostName, online2.HostName, unknown.HostName, offline.HostName },
            fx.Probe.Calls);
    }

    [Fact]
    public async Task One_broken_host_does_not_stop_the_search()
    {
        var fx = new Fixture();
        var probeFails = fx.AddPc("PC-1", [Session(1, "ivanov")]);
        var sessionsFail = fx.AddPc("PC-2", [Session(1, "ivanov")]);
        fx.AddPc("PC-3", [Session(1, "ivanov")]);
        fx.Probe.Throwing.Add(probeFails.HostName);
        fx.Sessions.Respond = host => host == sessionsFail.HostName
            ? throw new InvalidOperationException("RPC недоступен")
            : SessionListResult.Ok(new[] { Session(1, "ivanov") });

        var found = await fx.FindAsync("ivanov");

        Assert.Equal(new[] { "PC-3/ivanov/live" }, Summary(found));
    }

    [Fact]
    public async Task Host_names_that_fail_validation_are_not_queried()
    {
        var fx = new Fixture();
        fx.Index.Add(Pc("-evil", id: 90) with { DnsName = null });
        fx.AddPc("PC-1", [Session(1, "ivanov")]);

        var found = await fx.FindAsync("ivanov");

        Assert.Single(found);
        Assert.DoesNotContain("-evil", fx.Probe.Calls);
    }

    // ----- параллелизм ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    [InlineData(100, 32)] // не больше 32, сколько бы потоков ни было в настройках
    [InlineData(0, 1)]
    public async Task Concurrent_session_requests_are_limited_by_wts_threads_but_never_above_32(int wtsThreads, int expectedMax)
    {
        var fx = new Fixture();
        fx.Settings.Settings.WtsThreads = wtsThreads;
        for (var i = 1; i <= 60; i++) fx.AddPc($"PC-{i:00}", [Session(1, "someone")]);
        var gate = new Gate();
        fx.Sessions.Hold = (_, ct) => gate.WaitAsync(ct);

        var search = fx.FindAsync("ivanov");
        await Wait.UntilAsync(() => fx.Sessions.Calls.Count == expectedMax, what: "запросы сессий заняли все места");
        await Wait.HoldsAsync(() => fx.Sessions.Calls.Count == expectedMax && fx.Sessions.MaxConcurrent == expectedMax, 150, "лишний запрос не должен стартовать");

        gate.Open();
        await search.WithTimeout();

        Assert.Equal(60, fx.Sessions.Calls.Count);
        Assert.Equal(expectedMax, fx.Sessions.MaxConcurrent);
    }

    [Fact]
    public async Task Port_checks_are_limited_separately_by_probe_parallelism()
    {
        var fx = new Fixture();
        fx.Settings.Settings.OnlineProbeParallelism = 5;
        for (var i = 1; i <= 40; i++) fx.AddPc($"PC-{i:00}");
        var gate = new Gate();
        fx.Probe.Hold = (_, ct) => gate.WaitAsync(ct);

        var search = fx.FindAsync("ivanov");
        await Wait.UntilAsync(() => fx.Probe.Calls.Count == 5);
        await Wait.HoldsAsync(() => fx.Probe.Calls.Count == 5, 150);

        gate.Open();
        await search.WithTimeout();

        Assert.Equal(40, fx.Probe.Calls.Count);
        Assert.Equal(5, fx.Probe.MaxConcurrent);
    }

    [Fact]
    public async Task Waiting_for_a_session_slot_does_not_hold_up_port_checks_of_other_computers()
    {
        var fx = new Fixture();
        fx.Settings.Settings.WtsThreads = 1;
        fx.Settings.Settings.OnlineProbeParallelism = 64;
        for (var i = 1; i <= 10; i++) fx.AddPc($"PC-{i:00}", [Session(1, "someone")]);
        var gate = new Gate();
        fx.Sessions.Hold = (_, ct) => gate.WaitAsync(ct);

        var search = fx.FindAsync("ivanov");

        await Wait.UntilAsync(() => fx.Probe.Calls.Count == 10, what: "порты проверены у всех, хотя сессии пока опрашивает один");
        Assert.Single(fx.Sessions.Calls);

        gate.Open();
        await search.WithTimeout();
    }

    // ----- отмена ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_stops_the_search_cleanly_and_leaves_no_work_running()
    {
        var fx = new Fixture();
        for (var i = 1; i <= 50; i++) fx.AddPc($"PC-{i:00}", [Session(1, "ivanov")]);
        var never = new Gate();
        fx.Sessions.Hold = (_, ct) => never.WaitAsync(ct);
        using var cts = new CancellationTokenSource();

        var search = fx.FindAsync("ivanov", ct: cts.Token);
        await Wait.UntilAsync(() => fx.Sessions.Calls.Count >= 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WithTimeout());

        Assert.Equal(0, fx.Sessions.Active);
        Assert.Equal(0, fx.Probe.Active);
        var calls = fx.Sessions.Calls.Count;
        await Task.Delay(100);
        Assert.Equal(calls, fx.Sessions.Calls.Count);
        Assert.True(calls < 50, "после отмены оставшиеся компьютеры не опрашиваются");
    }

    [Fact]
    public async Task Stopping_enumeration_early_cancels_remaining_work()
    {
        var fx = new Fixture();
        var fast = fx.AddPc("PC-1", [Session(1, "ivanov")]);
        for (var i = 2; i <= 20; i++) fx.AddPc($"PC-{i:00}", [Session(1, "ivanov")]);
        var never = new Gate();
        fx.Sessions.Hold = (host, ct) => host == fast.HostName ? Task.CompletedTask : never.WaitAsync(ct);

        UserLocation? first = null;
        await foreach (var location in fx.Locator.FindAsync("ivanov", null, true, default))
        {
            first = location;
            break;
        }

        Assert.Equal("PC-1", first!.Computer.Name);
        Assert.Equal(0, fx.Sessions.Active); // выход из цикла дождался остановки опроса
        Assert.Equal(0, fx.Probe.Active);
        var calls = fx.Sessions.Calls.Count;
        await Task.Delay(100);
        Assert.Equal(calls, fx.Sessions.Calls.Count);
    }

    [Fact]
    public async Task Cancelled_token_before_start_throws_without_polling()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "ivanov")]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fx.FindAsync("ivanov", ct: cts.Token));

        Assert.Empty(fx.Sessions.Calls);
    }

    [Fact]
    public async Task Second_search_works_after_the_first_was_cancelled()
    {
        var fx = new Fixture();
        fx.AddPc("PC-1", [Session(1, "ivanov")]);
        var never = new Gate();
        fx.Sessions.Hold = (_, ct) => never.WaitAsync(ct);
        using var cts = new CancellationTokenSource();
        var first = fx.FindAsync("ivanov", ct: cts.Token);
        await Wait.UntilAsync(() => fx.Sessions.Calls.Count == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WithTimeout());
        fx.Sessions.Hold = null;

        var found = await fx.FindAsync("ivanov");

        Assert.Equal(new[] { "PC-1/ivanov/live" }, Summary(found));
    }
}
