using RemoteShadow.Core.Models;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

public class OnlineStatusServiceTests
{
    private sealed class Fixture
    {
        public Fixture()
        {
            Service = new OnlineStatusService(Index, Probe, Settings, Time);
        }

        public FakeComputerIndex Index { get; } = new();
        public FakeHostProbe Probe { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public OnlineStatusService Service { get; }

        public ComputerInfo[] AddComputers(int count, string prefix = "PC")
        {
            var computers = Enumerable.Range(1, count).Select(i => Pc($"{prefix}-{i:000}", id: i)).ToArray();
            Index.Add(computers);
            return computers;
        }

        public Task RefreshAsync(IReadOnlyCollection<ComputerInfo> computers, bool force = false, CancellationToken ct = default) =>
            Service.RefreshAsync(computers, force, ct);

        public bool? StatusOf(ComputerInfo computer) => Index.Find(computer.Id)?.IsOnline;
    }

    private static string HostOf(ComputerInfo computer) => computer.HostName;

    [Fact]
    public async Task Probes_each_enabled_computer_by_host_name_and_applies_results_to_the_index()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(3);
        var disabled = Pc("OFF-1", id: 50, enabled: false);
        fx.Index.Add(disabled);
        fx.Probe.Online = host => !host.StartsWith("pc-002");

        await fx.RefreshAsync(pcs.Append(disabled).ToArray());

        Assert.Equal(pcs.Select(HostOf).Order(), fx.Probe.Calls.Order());
        Assert.Contains("pc-001.corp.local", fx.Probe.Calls);
        Assert.True(fx.StatusOf(pcs[0]));
        Assert.False(fx.StatusOf(pcs[1]));
        Assert.True(fx.StatusOf(pcs[2]));
        Assert.Null(fx.StatusOf(disabled));
        Assert.DoesNotContain(fx.Index.SetOnlineCalls, call => call.Id == disabled.Id);
    }

    [Fact]
    public async Task Computer_without_dns_name_is_probed_by_short_name()
    {
        var fx = new Fixture();
        var pc = Pc("SHORT-1", id: 1) with { DnsName = null };
        fx.Index.Add(pc);

        await fx.RefreshAsync(new[] { pc });

        Assert.Equal(new[] { "SHORT-1" }, fx.Probe.Calls);
    }

    [Fact]
    public async Task Probe_timeout_comes_from_settings()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(2);

        await fx.RefreshAsync(pcs);
        fx.Settings.Settings.OnlineProbeTimeoutMs = 800;
        await fx.RefreshAsync(pcs, force: true);

        Assert.Equal(
            new[] { 500d, 500d, 800d, 800d },
            fx.Probe.Timeouts.Select(t => t.TotalMilliseconds));
    }

    [Fact]
    public async Task Empty_list_and_disabled_only_do_nothing()
    {
        var fx = new Fixture();

        await fx.RefreshAsync(Array.Empty<ComputerInfo>());
        await fx.RefreshAsync(new[] { Pc("OFF", id: 1, enabled: false) });

        Assert.Empty(fx.Probe.Calls);
        Assert.Empty(fx.Index.SetOnlineCalls);
    }

    [Fact]
    public async Task Same_computer_listed_twice_is_probed_once()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);

        await fx.RefreshAsync(new[] { pcs[0], pcs[0] });

        Assert.Single(fx.Probe.Calls);
    }

    // ----- кэш на 2 минуты -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Result_is_cached_for_two_minutes()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(3);
        await fx.RefreshAsync(pcs);
        Assert.Equal(3, fx.Probe.Calls.Count);

        await fx.RefreshAsync(pcs);
        fx.Time.Advance(TimeSpan.FromSeconds(119));
        await fx.RefreshAsync(pcs);
        Assert.Equal(3, fx.Probe.Calls.Count); // свежие результаты не перепроверяются

        fx.Time.Advance(TimeSpan.FromSeconds(1));
        await fx.RefreshAsync(pcs);
        Assert.Equal(6, fx.Probe.Calls.Count); // 120 секунд — кэш истёк
    }

    [Fact]
    public async Task Cache_age_is_counted_from_each_computers_own_check()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(2);
        await fx.RefreshAsync(new[] { pcs[0] });
        fx.Time.Advance(TimeSpan.FromSeconds(100));
        await fx.RefreshAsync(new[] { pcs[1] });
        fx.Time.Advance(TimeSpan.FromSeconds(30)); // у первого 130 секунд, у второго 30

        await fx.RefreshAsync(pcs);

        Assert.Equal(new[] { pcs[0].HostName, pcs[1].HostName, pcs[0].HostName }, fx.Probe.Calls);
    }

    [Fact]
    public async Task Force_ignores_the_cache()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(2);
        await fx.RefreshAsync(pcs);

        await fx.RefreshAsync(pcs, force: true);

        Assert.Equal(4, fx.Probe.Calls.Count);
    }

    [Fact]
    public async Task Cache_length_comes_from_settings()
    {
        var fx = new Fixture();
        fx.Settings.Settings.OnlineCacheSeconds = 10;
        var pcs = fx.AddComputers(1);
        await fx.RefreshAsync(pcs);

        fx.Time.Advance(TimeSpan.FromSeconds(9));
        await fx.RefreshAsync(pcs);
        Assert.Single(fx.Probe.Calls);

        fx.Time.Advance(TimeSpan.FromSeconds(1));
        await fx.RefreshAsync(pcs);
        Assert.Equal(2, fx.Probe.Calls.Count);
    }

    [Fact]
    public async Task Offline_result_is_cached_too()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);
        fx.Probe.Online = _ => false;
        await fx.RefreshAsync(pcs);

        await fx.RefreshAsync(pcs);

        Assert.Single(fx.Probe.Calls);
        Assert.False(fx.StatusOf(pcs[0]));
    }

    [Fact]
    public async Task Status_lost_after_index_was_cleared_is_probed_again_despite_cache()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(2);
        await fx.RefreshAsync(pcs);

        fx.Index.Clear();
        fx.Index.Add(pcs.Select(p => p with { IsOnline = null }).ToArray());
        await fx.RefreshAsync(pcs.Select(p => p with { IsOnline = null }).ToArray());

        Assert.Equal(4, fx.Probe.Calls.Count);
        Assert.True(fx.StatusOf(pcs[0]));
    }

    // ----- ограничение параллелизма ---------------------------------------------------------------------------------------

    [Fact]
    public async Task No_more_than_the_configured_number_of_probes_run_at_once()
    {
        var fx = new Fixture();
        fx.Settings.Settings.OnlineProbeParallelism = 4;
        var pcs = fx.AddComputers(30);
        var gate = new Gate();
        fx.Probe.Hold = (_, ct) => gate.WaitAsync(ct);

        var refresh = fx.RefreshAsync(pcs);
        await Wait.UntilAsync(() => fx.Probe.Calls.Count == 4, what: "первые четыре проверки");
        await Wait.HoldsAsync(() => fx.Probe.Calls.Count == 4 && fx.Probe.MaxConcurrent == 4, 150, "пятая не должна стартовать");

        gate.Open();
        await refresh.WithTimeout();

        Assert.Equal(30, fx.Probe.Calls.Count);
        Assert.Equal(4, fx.Probe.MaxConcurrent);
        Assert.All(pcs, pc => Assert.True(fx.StatusOf(pc)));
    }

    [Fact]
    public async Task Default_limit_is_64_probes_at_once()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(150);
        var gate = new Gate();
        fx.Probe.Hold = (_, ct) => gate.WaitAsync(ct);

        var refresh = fx.RefreshAsync(pcs);
        await Wait.UntilAsync(() => fx.Probe.Calls.Count == 64);
        await Wait.HoldsAsync(() => fx.Probe.Calls.Count == 64, 150);

        gate.Open();
        await refresh.WithTimeout();

        Assert.Equal(150, fx.Probe.Calls.Count);
        Assert.Equal(64, fx.Probe.MaxConcurrent);
    }

    [Fact]
    public async Task Zero_parallelism_setting_is_treated_as_one()
    {
        var fx = new Fixture();
        fx.Settings.Settings.OnlineProbeParallelism = 0;
        var pcs = fx.AddComputers(5);

        await fx.RefreshAsync(pcs);

        Assert.Equal(5, fx.Probe.Calls.Count);
        Assert.Equal(1, fx.Probe.MaxConcurrent);
    }

    // ----- пачки ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Results_reach_the_index_in_batches_every_200_ms_and_leftovers_at_the_end()
    {
        var fx = new Fixture();
        fx.Settings.Settings.OnlineProbeParallelism = 2;
        var pcs = fx.AddComputers(4);
        var slow = new Gate();
        fx.Probe.Hold = (host, ct) => host == pcs[1].HostName ? slow.WaitAsync(ct) : Task.CompletedTask;

        var refresh = fx.RefreshAsync(pcs);
        await Wait.UntilAsync(() => fx.Probe.Calls.Count == 4, what: "все четыре проверки начались");

        // Три результата готовы, но по одному в индекс они не уходят.
        await Wait.HoldsAsync(() => fx.Index.SetOnlineCalls.Count == 0, 100, "результаты копятся до сброса");

        fx.Time.Advance(TimeSpan.FromMilliseconds(200));
        await Wait.UntilAsync(() => fx.Index.SetOnlineCalls.Count == 3, what: "первая пачка");
        Assert.DoesNotContain(fx.Index.SetOnlineCalls, call => call.Id == pcs[1].Id);

        slow.Open();
        await refresh.WithTimeout();

        Assert.Equal(4, fx.Index.SetOnlineCalls.Count);
        Assert.Equal(pcs.Select(p => p.Id).Order(), fx.Index.SetOnlineCalls.Select(c => c.Id).Order());
        Assert.Equal(0, fx.Time.ActiveTimers); // таймер сброса остановлен
    }

    // ----- ошибки и отмена -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Probe_exception_for_one_host_means_offline_and_does_not_stop_the_rest()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(3);
        fx.Probe.Throwing.Add(pcs[1].HostName);

        await fx.RefreshAsync(pcs);

        Assert.True(fx.StatusOf(pcs[0]));
        Assert.False(fx.StatusOf(pcs[1]));
        Assert.True(fx.StatusOf(pcs[2]));
    }

    [Fact]
    public async Task Host_name_that_looks_like_a_command_line_switch_is_offline_and_never_reaches_the_probe()
    {
        var fx = new Fixture();
        var evil = Pc("-evil", id: 1) with { DnsName = null };
        var spaced = Pc("PC 2", id: 2) with { DnsName = null };
        fx.Index.Add(evil, spaced);

        await fx.RefreshAsync(new[] { evil, spaced });

        Assert.Empty(fx.Probe.Calls);
        Assert.False(fx.StatusOf(evil));
        Assert.False(fx.StatusOf(spaced));
    }

    [Fact]
    public async Task Cancellation_stops_new_probes_applies_finished_results_and_leaves_no_timer()
    {
        var fx = new Fixture();
        fx.Settings.Settings.OnlineProbeParallelism = 2;
        var pcs = fx.AddComputers(100);
        var hold = new Gate();
        // Первая проверка отвечает сразу, остальные висят.
        var calls = 0;
        fx.Probe.Hold = (_, ct) => Interlocked.Increment(ref calls) == 1 ? Task.CompletedTask : hold.WaitAsync(ct);
        using var cts = new CancellationTokenSource();

        var refresh = fx.RefreshAsync(pcs, ct: cts.Token);
        await Wait.UntilAsync(() => fx.Probe.Calls.Count >= 3, what: "две проверки висят, одна завершилась");
        var started = fx.Probe.Calls.Count;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WithTimeout());

        Assert.True(fx.Probe.Calls.Count <= started + 1, "после отмены новые проверки не стартуют");
        Assert.Equal(0, fx.Time.ActiveTimers);
        var applied = fx.Index.SetOnlineCalls.Count;
        Assert.True(applied >= 1, "то, что успели проверить, не теряется");

        fx.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(applied, fx.Index.SetOnlineCalls.Count);
        Assert.DoesNotContain(fx.Index.SetOnlineCalls, call => call.Online is null);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_without_probing()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(3);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fx.RefreshAsync(pcs, ct: cts.Token));

        Assert.Empty(fx.Probe.Calls);
    }

    [Fact]
    public async Task Cancelled_probes_are_not_cached_as_offline()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(2);
        var hold = new Gate();
        fx.Probe.Hold = (_, ct) => hold.WaitAsync(ct);
        using var cts = new CancellationTokenSource();
        var refresh = fx.RefreshAsync(pcs, ct: cts.Token);
        await Wait.UntilAsync(() => fx.Probe.Calls.Count == 2);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WithTimeout());
        fx.Probe.Hold = null;

        await fx.RefreshAsync(pcs); // кэша нет: результаты отменённых проверок не сохранились

        Assert.Equal(4, fx.Probe.Calls.Count);
        Assert.All(pcs, pc => Assert.True(fx.StatusOf(pc)));
    }

    [Fact]
    public async Task Two_overlapping_calls_do_not_corrupt_results()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(200);
        fx.Probe.Online = host => host.GetHashCode() % 2 == 0;
        fx.Probe.Hold = async (_, _) => await Task.Yield();

        await Task.WhenAll(fx.RefreshAsync(pcs, force: true), fx.RefreshAsync(pcs, force: true));

        foreach (var pc in pcs)
            Assert.Equal(fx.Probe.Online(pc.HostName), fx.StatusOf(pc));
    }
}
