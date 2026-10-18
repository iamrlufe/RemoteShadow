using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

public class ComputerIndexRefreshTests
{
    private static readonly DateTime Start = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static string[] Names(IEnumerable<ComputerInfo> computers) => computers.Select(c => c.Name).ToArray();

    private static CancellationToken NoToken => CancellationToken.None;

    /// <summary>Изменение в AD, которое увидит частичное обновление: whenChanged внутри окна «последняя синхронизация − 5 минут».</summary>
    private static DateTime ChangedAt(IndexFixture fx) => fx.Time.GetUtcNow().UtcDateTime;

    private static void Replace(IndexFixture fx, Guid id, Func<ComputerInfo, ComputerInfo> change)
    {
        var i = fx.Ad.Computers.FindIndex(c => c.Id == id);
        fx.Ad.Computers[i] = change(fx.Ad.Computers[i]);
    }

    // ----- полная и частичная загрузка ---------------------------------------------------------------------------------

    [Fact]
    public async Task Full_refresh_loads_everything_and_remembers_sync_time()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2), Pc("PC-3", id: 3));

        await fx.Index.RefreshAsync(full: true, NoToken);

        Assert.Equal(new[] { "PC-1", "PC-2", "PC-3" }, Names(fx.Index.All));
        Assert.Equal(Start, fx.Index.LastSyncUtc);
        Assert.Null(fx.Index.LastError);
        Assert.False(fx.Index.IsLoading);
        var query = Assert.Single(fx.Ad.Queries);
        Assert.Null(query.ChangedSince);
        Assert.False(query.IncludeDisabled);
    }

    [Fact]
    public async Task Partial_request_before_any_load_is_upgraded_to_full()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[0].ChangedSince);
        Assert.Single(fx.Index.All);
    }

    [Fact]
    public async Task Partial_refresh_asks_for_changes_since_last_sync_minus_five_minutes()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        fx.Time.Advance(TimeSpan.FromMinutes(7));

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Equal(Start.AddMinutes(-5), fx.Ad.Queries[1].ChangedSince);
        Assert.Equal(Start.AddMinutes(7), fx.Index.LastSyncUtc);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Equal(Start.AddMinutes(7 - 5), fx.Ad.Queries[2].ChangedSince);
    }

    [Fact]
    public async Task Partial_refresh_adds_new_and_updates_changed_computers()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1, description: "старое"), Pc("PC-2", id: 2));
        await fx.FullLoadAsync();
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        Replace(fx, IdOf(1), c => c with { Description = "новое", WhenChanged = ChangedAt(fx) });
        fx.Ad.Computers.Add(Pc("PC-3", id: 3, whenChanged: ChangedAt(fx)));

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Equal(new[] { "PC-1", "PC-2", "PC-3" }, Names(fx.Index.All));
        Assert.Equal("новое", fx.Index.Find(IdOf(1))!.Description);
        Assert.Equal(new[] { "PC-1" }, Names(fx.Index.Search("новое")));
        Assert.Empty(fx.Index.Search("старое"));
    }

    [Fact]
    public async Task Rename_and_move_replace_the_record_by_id_instead_of_adding_a_new_one()
    {
        using var fx = new IndexFixture().Seed(
            Pc("OLD-NAME", AccountingOu, id: 1),
            Pc("PC-2", WarehouseOu, id: 2));
        await fx.FullLoadAsync();
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        Replace(fx, IdOf(1), c => c with
        {
            Name = "NEW-NAME",
            DnsName = "new-name.corp.local",
            DistinguishedName = $"CN=NEW-NAME,{WarehouseOu}",
            WhenChanged = ChangedAt(fx),
        });

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Equal(2, fx.Index.All.Count);
        Assert.Equal("NEW-NAME", fx.Index.Find(IdOf(1))!.Name);
        Assert.Empty(fx.Index.Search("OLD-NAME"));
        Assert.Equal(new[] { "NEW-NAME" }, Names(fx.Index.Search("new-name")));
        Assert.Equal(new[] { "NEW-NAME", "PC-2" }, Names(fx.Index.GetByOu(WarehouseOu, includeNested: false)));
        Assert.Empty(fx.Index.GetByOu(AccountingOu, includeNested: true));
    }

    [Fact]
    public async Task Computer_deleted_in_ad_disappears_only_after_full_refresh()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2), Pc("PC-3", id: 3));
        await fx.FullLoadAsync();
        fx.Ad.Computers.RemoveAll(c => c.Id == IdOf(2));
        fx.Time.Advance(TimeSpan.FromMinutes(5));

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.NotNull(fx.Ad.Queries[1].ChangedSince);
        Assert.Equal(new[] { "PC-1", "PC-2", "PC-3" }, Names(fx.Index.All));

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: true, NoToken);

        Assert.Null(fx.Ad.Queries[2].ChangedSince);
        Assert.Equal(new[] { "PC-1", "PC-3" }, Names(fx.Index.All));
        Assert.Null(fx.Index.Find(IdOf(2)));
        Assert.Empty(fx.Index.Search("PC-2"));
    }

    // ----- когда обновление принудительно полное -----------------------------------------------------------------------

    [Fact]
    public async Task Full_refresh_is_forced_after_an_hour_since_the_last_full()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2));
        await fx.FullLoadAsync();

        fx.Time.Advance(TimeSpan.FromMinutes(59));
        await fx.Index.RefreshAsync(full: false, NoToken);
        Assert.NotNull(fx.Ad.Queries[1].ChangedSince); // ещё частичное

        fx.Ad.Computers.RemoveAll(c => c.Id == IdOf(2));
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[2].ChangedSince); // прошёл час с последней полной
        Assert.Equal(new[] { "PC-1" }, Names(fx.Index.All));
    }

    [Fact]
    public async Task Partial_refreshes_do_not_postpone_the_hourly_full_refresh()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();

        for (var i = 0; i < 11; i++)
        {
            fx.Time.Advance(TimeSpan.FromMinutes(5));
            await fx.Index.RefreshAsync(full: false, NoToken);
        }

        Assert.All(fx.Ad.Queries.Skip(1), q => Assert.NotNull(q.ChangedSince));

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[^1].ChangedSince);
    }

    [Fact]
    public async Task Interval_comes_from_settings()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        fx.Settings.Settings.FullRefreshMinutes = 10;
        await fx.FullLoadAsync();

        fx.Time.Advance(TimeSpan.FromMinutes(9));
        await fx.Index.RefreshAsync(full: false, NoToken);
        Assert.NotNull(fx.Ad.Queries[1].ChangedSince);

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Index.RefreshAsync(full: false, NoToken);
        Assert.Null(fx.Ad.Queries[2].ChangedSince);
    }

    [Fact]
    public async Task Domain_controller_change_forces_full_refresh_once()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2));
        await fx.FullLoadAsync();
        fx.Ad.Computers.RemoveAll(c => c.Id == IdOf(2));
        fx.Ad.DomainController = "dc2.corp.local";
        fx.Time.Advance(TimeSpan.FromMinutes(5));

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[1].ChangedSince);
        Assert.Equal(new[] { "PC-1" }, Names(fx.Index.All));

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.NotNull(fx.Ad.Queries[2].ChangedSince); // на новом контроллере дальше снова частичные
    }

    [Fact]
    public async Task Domain_controller_name_is_compared_ignoring_case()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        fx.Ad.DomainController = "DC1.CORP.LOCAL";
        fx.Time.Advance(TimeSpan.FromMinutes(5));

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.NotNull(fx.Ad.Queries[1].ChangedSince);
    }

    [Fact]
    public async Task Controller_that_appears_only_during_a_partial_refresh_forces_full_next_time()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();

        // Каталог не сообщает контроллер до подключения, а в ходе запроса переключается на другой.
        fx.Ad.DomainController = null;
        fx.Ad.AfterPage = (_, _) =>
        {
            fx.Ad.DomainController = "dc2.corp.local";
            return Task.CompletedTask;
        };
        fx.Ad.PageSize = 1;
        fx.Ad.Computers[0] = fx.Ad.Computers[0] with { WhenChanged = Start.AddMinutes(1) };
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, NoToken);
        fx.Ad.AfterPage = null;

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        fx.Ad.DomainController = null;
        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[2].ChangedSince);
    }

    [Fact]
    public async Task Switching_show_disabled_on_and_off_forces_full_refresh_each_time()
    {
        using var fx = new IndexFixture().Seed(
            Pc("PC-1", id: 1),
            Pc("OFF-1", id: 2, enabled: false));
        await fx.FullLoadAsync();
        Assert.Equal(new[] { "PC-1" }, Names(fx.Index.All));

        fx.Settings.Settings.ShowDisabledComputers = true;
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[1].ChangedSince);
        Assert.True(fx.Ad.Queries[1].IncludeDisabled);
        Assert.Equal(new[] { "OFF-1", "PC-1" }, Names(fx.Index.All));

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, NoToken);
        Assert.NotNull(fx.Ad.Queries[2].ChangedSince); // настройка не менялась — снова частичное
        Assert.True(fx.Ad.Queries[2].IncludeDisabled);

        fx.Settings.Settings.ShowDisabledComputers = false;
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[3].ChangedSince);
        Assert.False(fx.Ad.Queries[3].IncludeDisabled);
        Assert.Equal(new[] { "PC-1" }, Names(fx.Index.All));
    }

    [Theory]
    [InlineData(true, false, false, false, false)] // запрошена полная
    [InlineData(false, true, false, false, true)] // ещё не было синхронизации
    [InlineData(false, false, true, false, true)] // ещё не было полной
    [InlineData(false, false, false, false, false)] // всё спокойно
    [InlineData(false, false, false, true, true)] // изменилась настройка «отключённые»
    public void MustBeFull_basic_rules(bool requested, bool noSync, bool noFullSync, bool showDisabledChanged, bool expectedWhenNotRequested)
    {
        var now = Start.AddMinutes(10);
        var result = ComputerIndex.MustBeFull(
            requested,
            lastSync: noSync ? null : Start.AddMinutes(5),
            lastFullSync: noFullSync ? null : Start,
            syncedDomainController: "dc1",
            fullShowDisabled: false,
            nowUtc: now,
            currentDomainController: "dc1",
            showDisabled: showDisabledChanged,
            fullInterval: TimeSpan.FromMinutes(60));

        Assert.Equal(requested || expectedWhenNotRequested, result);
    }

    [Theory]
    [InlineData("dc1", "dc1", false)]
    [InlineData("DC1", "dc1", false)]
    [InlineData("dc2", "dc1", true)]
    [InlineData(null, "dc1", false)] // каталог пока не знает контроллер
    [InlineData("dc1", null, false)] // в кэше контроллера нет
    [InlineData(null, null, false)]
    public void MustBeFull_compares_domain_controllers(string? current, string? synced, bool expected)
    {
        var result = ComputerIndex.MustBeFull(
            requested: false,
            lastSync: Start,
            lastFullSync: Start,
            syncedDomainController: synced,
            fullShowDisabled: false,
            nowUtc: Start.AddMinutes(5),
            currentDomainController: current,
            showDisabled: false,
            fullInterval: TimeSpan.FromMinutes(60));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(61, true)]
    [InlineData(-1, true)] // часы ушли назад: доверять меткам нельзя
    public void MustBeFull_after_interval(int minutesSinceFull, bool expected)
    {
        var result = ComputerIndex.MustBeFull(
            requested: false,
            lastSync: Start,
            lastFullSync: Start,
            syncedDomainController: "dc1",
            fullShowDisabled: false,
            nowUtc: Start.AddMinutes(minutesSinceFull),
            currentDomainController: "dc1",
            showDisabled: false,
            fullInterval: TimeSpan.FromMinutes(60));

        Assert.Equal(expected, result);
    }

    [Theory]
    // full, previous, before, after => ожидаемый контроллер
    [InlineData(true, "dc1", "dc2", "dc2", "dc2")]
    [InlineData(true, "dc1", null, "dc2", "dc2")]
    [InlineData(true, "dc1", "dc2", null, "dc2")]
    [InlineData(true, "dc1", null, null, "dc1")]
    [InlineData(false, "dc1", "dc1", "dc1", "dc1")]
    [InlineData(false, "dc1", "dc1", "dc2", "dc1")] // сменился на ходу
    [InlineData(false, "dc1", null, "dc2", "dc1")] // каталог не сказал заранее
    [InlineData(false, "dc1", "dc1", null, "dc1")]
    [InlineData(false, null, null, "dc1", "dc1")]
    [InlineData(false, null, "dc1", "dc2", "dc1")]
    [InlineData(false, null, null, null, null)]
    public void Synced_domain_controller_resolution(bool full, string? previous, string? before, string? after, string? expected) =>
        Assert.Equal(expected, ComputerIndex.ResolveSyncedDomainController(full, previous, before, after));

    // ----- ошибки ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Operation_failed_exception_text_is_kept_as_is_and_old_data_stays()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2));
        await fx.FullLoadAsync();
        var syncedAt = fx.Index.LastSyncUtc;
        fx.Ad.FailWith = new OperationFailedException("Контроллер домена недоступен (0x51).", OperationFailure.Unreachable);
        fx.Time.Advance(TimeSpan.FromMinutes(5));

        await fx.Index.RefreshAsync(full: true, NoToken);

        Assert.Equal("Контроллер домена недоступен (0x51).", fx.Index.LastError);
        Assert.Equal(new[] { "PC-1", "PC-2" }, Names(fx.Index.All));
        Assert.Equal(syncedAt, fx.Index.LastSyncUtc);
        Assert.False(fx.Index.IsLoading);
    }

    [Fact]
    public async Task Unexpected_exception_gets_russian_prefix_and_next_success_clears_error()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        fx.Ad.FailWith = new InvalidOperationException("что-то пошло не так");

        await fx.FullLoadAsync();

        Assert.Equal("Не удалось обновить список компьютеров: что-то пошло не так", fx.Index.LastError);
        Assert.Empty(fx.Index.All);
        Assert.Null(fx.Index.LastSyncUtc);

        fx.Ad.FailWith = null;
        await fx.FullLoadAsync();

        Assert.Null(fx.Index.LastError);
        Assert.Single(fx.Index.All);
    }

    [Fact]
    public async Task Failure_midway_through_full_refresh_deletes_nothing()
    {
        using var fx = new IndexFixture().Seed(
            Pc("PC-1", id: 1), Pc("PC-2", id: 2), Pc("PC-3", id: 3), Pc("PC-4", id: 4), Pc("PC-5", id: 5));
        await fx.FullLoadAsync();
        fx.Ad.Computers.RemoveAll(c => c.Id == IdOf(1));
        fx.Ad.Computers.Insert(0, Pc("NEW-6", id: 6));
        fx.Ad.PageSize = 1;
        fx.Ad.FailAfter = (3, new IOException("обрыв связи"));
        // Часть записей успевает опубликоваться: время идёт, пока приходят страницы.
        fx.Ad.AfterPage = (_, _) =>
        {
            fx.Time.Advance(TimeSpan.FromMilliseconds(300));
            return Task.CompletedTask;
        };

        await fx.FullLoadAsync();

        Assert.StartsWith("Не удалось обновить список компьютеров:", fx.Index.LastError);
        Assert.Contains("NEW-6", Names(fx.Index.All)); // опубликовано до сбоя
        Assert.Equal(5, fx.Index.All.Count(c => c.Name.StartsWith("PC-"))); // PC-1 не удалён: удаление только по итогам полной загрузки
        Assert.Equal(Start, fx.Index.LastSyncUtc);
    }

    [Fact]
    public async Task Cancelled_refresh_throws_and_leaves_state_intact()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task;
        fx.Ad.ResetEntered();
        using var cts = new CancellationTokenSource();

        var refresh = fx.Index.RefreshAsync(full: true, cts.Token);
        await fx.Ad.Entered.Task.WithTimeout();
        Assert.True(fx.Index.IsLoading);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WithTimeout());
        Assert.False(fx.Index.IsLoading);
        Assert.Null(fx.Index.LastError);
        Assert.Single(fx.Index.All);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_before_any_query()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fx.Index.RefreshAsync(full: true, cts.Token));

        Assert.Equal(0, fx.Ad.EnumerateCalls);
    }

    // ----- одновременные вызовы и состояние загрузки --------------------------------------------------------------------

    [Fact]
    public async Task IsLoading_is_true_only_while_refresh_runs()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task;
        Assert.False(fx.Index.IsLoading);

        var refresh = fx.FullLoadAsync();
        await fx.Ad.Entered.Task.WithTimeout();
        Assert.True(fx.Index.IsLoading);

        gate.SetResult();
        await refresh.WithTimeout();

        Assert.False(fx.Index.IsLoading);
    }

    [Fact]
    public async Task Concurrent_partial_calls_run_one_query()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task;
        fx.Ad.ResetEntered();

        var first = fx.Index.RefreshAsync(full: false, NoToken);
        await fx.Ad.Entered.Task.WithTimeout();
        var second = fx.Index.RefreshAsync(full: false, NoToken);
        var third = fx.Index.RefreshAsync(full: false, NoToken);
        await Wait.HoldsAsync(() => !second.IsCompleted && !third.IsCompleted, 100, "повторный вызов должен ждать");

        gate.SetResult();
        await Task.WhenAll(first, second, third).WithTimeout();

        Assert.Equal(2, fx.Ad.EnumerateCalls); // начальная полная и одна частичная
    }

    [Fact]
    public async Task Full_request_waits_for_running_partial_and_then_still_runs()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task;
        fx.Ad.ResetEntered();

        var partial = fx.Index.RefreshAsync(full: false, NoToken);
        await fx.Ad.Entered.Task.WithTimeout();
        var full = fx.Index.RefreshAsync(full: true, NoToken);
        await Wait.HoldsAsync(() => !full.IsCompleted, 100);

        gate.SetResult();
        await Task.WhenAll(partial, full).WithTimeout();

        Assert.Equal(3, fx.Ad.EnumerateCalls);
        Assert.NotNull(fx.Ad.Queries[1].ChangedSince);
        Assert.Null(fx.Ad.Queries[2].ChangedSince);
    }

    [Fact]
    public async Task Call_after_a_failed_refresh_is_not_skipped()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task;
        fx.Ad.FailOnCall = call => call == 2 ? new InvalidOperationException("сбой") : null; // 1 — начальная полная
        fx.Ad.ResetEntered();

        var first = fx.Index.RefreshAsync(full: false, NoToken);
        await fx.Ad.Entered.Task.WithTimeout();
        var second = fx.Index.RefreshAsync(full: false, NoToken);
        gate.SetResult();
        await Task.WhenAll(first, second).WithTimeout();

        Assert.Equal(3, fx.Ad.EnumerateCalls);
        Assert.Null(fx.Index.LastError);
    }

    // ----- события ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Changed_is_published_in_batches_not_for_every_record_and_partial_data_is_visible()
    {
        using var fx = new IndexFixture();
        for (var i = 1; i <= 1000; i++) fx.Ad.Computers.Add(Pc($"PC-{i:0000}", id: i));
        fx.Ad.PageSize = 1;
        var changes = 0;
        fx.Index.Changed += (_, _) => Interlocked.Increment(ref changes);
        var seen = new List<int>();
        fx.Ad.AfterPage = (page, _) =>
        {
            fx.Time.Advance(TimeSpan.FromMilliseconds(50));
            if (page % 100 == 99) seen.Add(fx.Index.All.Count);
            return Task.CompletedTask;
        };

        await fx.FullLoadAsync();

        // 1000 записей по 50 мс = 50 с виртуального времени: не чаще 5 событий в секунду, то есть около 250, плюс итоговое.
        Assert.InRange(changes, 100, 270);
        Assert.Equal(1000, fx.Index.All.Count);
        Assert.Equal(10, seen.Count);
        Assert.All(seen, count => Assert.InRange(count, 1, 999));
        Assert.Equal(seen.OrderBy(c => c), seen);
        Assert.True(seen[0] < seen[^1]);
    }

    [Fact]
    public async Task Changed_announces_loading_start_and_then_once_more_when_everything_arrived_at_once()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2));
        var states = new List<(bool Loading, int Count)>();
        fx.Index.Changed += (_, _) => states.Add((fx.Index.IsLoading, fx.Index.All.Count));

        await fx.FullLoadAsync();

        Assert.Equal(new[] { (true, 0), (false, 2) }, states);
    }

    [Fact]
    public async Task Loading_indicator_change_is_announced_before_the_first_records_arrive()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task;
        var loadingSeen = 0;
        fx.Index.Changed += (_, _) =>
        {
            if (fx.Index.IsLoading) Interlocked.Increment(ref loadingSeen);
        };

        var refresh = fx.FullLoadAsync();
        await fx.Ad.Entered.Task.WithTimeout();

        Assert.Equal(1, Volatile.Read(ref loadingSeen)); // AD ещё молчит, а подписчик уже знает о загрузке
        gate.SetResult();
        await refresh.WithTimeout();
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_break_refresh()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        fx.Index.Changed += (_, _) => throw new InvalidOperationException("подписчик");

        await fx.FullLoadAsync();

        Assert.Single(fx.Index.All);
        Assert.Null(fx.Index.LastError);
    }

    // ----- статус «в сети» ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetOnline_changes_only_the_status_and_raises_changed()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", AccountingOu, id: 1, description: "кабинет 5"), Pc("PC-2", id: 2));
        await fx.FullLoadAsync();
        var before = fx.Index.Find(IdOf(1))!;
        var changes = 0;
        fx.Index.Changed += (_, _) => changes++;

        fx.Index.SetOnline(IdOf(1), true);

        var after = fx.Index.Find(IdOf(1))!;
        Assert.True(after.IsOnline);
        Assert.Equal(before with { IsOnline = true }, after);
        Assert.Null(fx.Index.Find(IdOf(2))!.IsOnline);
        Assert.Equal(1, changes);
        Assert.Equal(new[] { "PC-1" }, Names(fx.Index.Search("кабинет")));
        Assert.True(fx.Index.GetByOu(AccountingOu, false).Single(c => c.Id == IdOf(1)).IsOnline);
        Assert.True(fx.Index.All.Single(c => c.Id == IdOf(1)).IsOnline);
        Assert.True(fx.Index.Search("pc-1").Single().IsOnline);
    }

    [Fact]
    public async Task SetOnline_with_same_value_or_unknown_id_does_nothing()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1, online: true));
        await fx.FullLoadAsync();
        var changes = 0;
        fx.Index.Changed += (_, _) => changes++;

        fx.Index.SetOnline(IdOf(1), true);
        fx.Index.SetOnline(IdOf(404), true);

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task SetOnline_can_reset_status_to_unknown()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        fx.Index.SetOnline(IdOf(1), false);
        Assert.False(fx.Index.Find(IdOf(1))!.IsOnline);

        fx.Index.SetOnline(IdOf(1), null);

        Assert.Null(fx.Index.Find(IdOf(1))!.IsOnline);
    }

    [Fact]
    public async Task Many_SetOnline_calls_are_coalesced_into_few_changed_events()
    {
        using var fx = new IndexFixture();
        for (var i = 1; i <= 100; i++) fx.Ad.Computers.Add(Pc($"PC-{i:000}", id: i));
        await fx.FullLoadAsync();
        var changes = 0;
        fx.Index.Changed += (_, _) => Interlocked.Increment(ref changes);

        for (var i = 1; i <= 100; i++) fx.Index.SetOnline(IdOf(i), true);

        Assert.Equal(1, changes); // первое событие сразу, остальные ждут
        Assert.All(fx.Index.All, c => Assert.True(c.IsOnline));

        fx.Time.Advance(TimeSpan.FromMilliseconds(200));
        await Wait.UntilAsync(() => Volatile.Read(ref changes) == 2, what: "слитое событие после паузы");

        fx.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.HoldsAsync(() => Volatile.Read(ref changes) == 2, 100, "лишних событий нет");

        fx.Index.SetOnline(IdOf(1), false); // после паузы событие снова идёт сразу
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task Online_status_survives_partial_and_full_refresh()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2));
        await fx.FullLoadAsync();
        fx.Index.SetOnline(IdOf(1), true);
        fx.Index.SetOnline(IdOf(2), false);
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        Replace(fx, IdOf(1), c => c with { Description = "изменён", WhenChanged = ChangedAt(fx) });

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Equal("изменён", fx.Index.Find(IdOf(1))!.Description);
        Assert.True(fx.Index.Find(IdOf(1))!.IsOnline);
        Assert.False(fx.Index.Find(IdOf(2))!.IsOnline);

        await fx.Index.RefreshAsync(full: true, NoToken);

        Assert.True(fx.Index.Find(IdOf(1))!.IsOnline);
        Assert.False(fx.Index.Find(IdOf(2))!.IsOnline);
    }

    // ----- очистка ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Clear_drops_data_and_sync_marks_and_next_refresh_is_full()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.FullLoadAsync();
        fx.Index.SetOnline(IdOf(1), true);
        var changes = 0;
        fx.Index.Changed += (_, _) => changes++;

        fx.Index.Clear();

        Assert.Empty(fx.Index.All);
        Assert.Empty(fx.Index.Search("pc"));
        Assert.Empty(fx.Index.GetByOu(AccountingOu, true));
        Assert.Null(fx.Index.Find(IdOf(1)));
        Assert.Null(fx.Index.LastSyncUtc);
        Assert.Null(fx.Index.LastError);
        Assert.Equal(1, changes);

        await fx.Index.RefreshAsync(full: false, NoToken);

        Assert.Null(fx.Ad.Queries[^1].ChangedSince);
        Assert.Single(fx.Index.All);
        Assert.Null(fx.Index.Find(IdOf(1))!.IsOnline);
    }

    [Fact]
    public async Task Clear_clears_last_error()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        fx.Ad.FailWith = new InvalidOperationException("сбой");
        await fx.FullLoadAsync();
        Assert.NotNull(fx.Index.LastError);

        fx.Index.Clear();

        Assert.Null(fx.Index.LastError);
    }

    [Fact]
    public async Task Clear_during_refresh_cancels_it_quietly_and_publishes_nothing()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1), Pc("PC-2", id: 2));
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task;

        var refresh = fx.FullLoadAsync();
        await fx.Ad.Entered.Task.WithTimeout();
        fx.Index.Clear();
        gate.SetResult();

        await refresh.WithTimeout(); // не бросает: отменил не вызывающий

        Assert.Empty(fx.Index.All);
        Assert.Null(fx.Index.LastSyncUtc);
        Assert.Null(fx.Index.LastError);
        Assert.False(fx.Index.IsLoading);
    }

    [Fact]
    public async Task Clear_during_publishing_does_not_leak_records_of_old_account()
    {
        using var fx = new IndexFixture();
        for (var i = 1; i <= 50; i++) fx.Ad.Computers.Add(Pc($"PC-{i:00}", id: i));
        fx.Ad.PageSize = 10;
        fx.Ad.AfterPage = (page, _) =>
        {
            fx.Time.Advance(TimeSpan.FromMilliseconds(250)); // публикация после каждой страницы
            if (page == 1) fx.Index.Clear();
            return Task.CompletedTask;
        };

        await fx.FullLoadAsync();

        Assert.Empty(fx.Index.All);
        Assert.Null(fx.Index.LastSyncUtc);
    }

    // ----- несколько потоков ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Readers_never_fail_while_the_index_is_being_updated()
    {
        using var fx = new IndexFixture();
        for (var i = 1; i <= 2000; i++) fx.Ad.Computers.Add(Pc($"PC-{i:0000}", i % 2 == 0 ? AccountingOu : WarehouseOu, id: i));
        await fx.FullLoadAsync();

        using var stop = new CancellationTokenSource();
        var failures = new List<Exception>();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var found = fx.Index.Search("pc-01", 20);
                    Assert.All(found, c => Assert.Contains("pc-01", c.Name, StringComparison.OrdinalIgnoreCase));

                    var all = fx.Index.All;
                    var count = all.Count;
                    var enumerated = all.Count(c => c.Name.Length > 0);
                    Assert.Equal(count, enumerated);

                    var ou = fx.Index.GetByOu(AccountingOu, false);
                    Assert.All(ou, c => Assert.Equal(AccountingOu, c.ParentDn));
                }
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add(ex);
            }
        })).ToArray();

        var writers = Task.Run(async () =>
        {
            for (var round = 0; round < 30; round++)
            {
                fx.Time.Advance(TimeSpan.FromSeconds(30));
                fx.Ad.Computers.Add(Pc($"NEW-{round:00}", id: 10_000 + round, whenChanged: fx.Time.GetUtcNow().UtcDateTime));
                await fx.Index.RefreshAsync(full: round % 5 == 0, NoToken);
                for (var i = 1; i <= 200; i++) fx.Index.SetOnline(IdOf(i), round % 2 == 0);
            }
        });

        await writers.WithTimeout(60_000);
        await stop.CancelAsync();
        await Task.WhenAll(readers).WithTimeout();

        Assert.Empty(failures);
        Assert.Equal(2030, fx.Index.All.Count);
    }
}
