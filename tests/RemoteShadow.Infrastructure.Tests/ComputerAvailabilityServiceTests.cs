using RemoteShadow.Core.Models;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

public class ComputerAvailabilityServiceTests
{
    private sealed class Fixture
    {
        public Fixture()
        {
            Settings.Settings.CheckIcmp = true;
            Settings.Settings.CheckRdp = true;
            Settings.Settings.CheckSmb = true;
            Settings.Settings.CheckSsh = false; // по умолчанию ssh выключен, как в AppSettings
            Service = new ComputerAvailabilityService(Index, Tcp, Icmp, Settings, Time);
        }

        public FakeComputerIndex Index { get; } = new();
        public FakeHostProbe Tcp { get; } = new();
        public FakeIcmpProbe Icmp { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public ComputerAvailabilityService Service { get; }

        public ComputerInfo[] AddComputers(int count, string prefix = "PC")
        {
            var computers = Enumerable.Range(1, count).Select(i => Pc($"{prefix}-{i:000}", id: i)).ToArray();
            Index.Add(computers);
            return computers;
        }

        public Task RefreshAsync(IReadOnlyCollection<ComputerInfo> computers, bool force = false, CancellationToken ct = default) =>
            Service.RefreshAsync(computers, force, ct);

        public ComputerInfo? StateOf(ComputerInfo computer) => Index.Find(computer.Id);
    }

    [Fact]
    public async Task Online_when_at_least_one_enabled_protocol_responds()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);
        fx.Tcp.Online = _ => false;
        fx.Icmp.Online = _ => true;

        await fx.RefreshAsync(pcs);

        var state = fx.StateOf(pcs[0])!;
        Assert.True(state.IsOnline);
        Assert.True(state.IcmpAvailable);
        Assert.False(state.RdpAvailable);
        Assert.False(state.SmbAvailable);
        Assert.Null(state.SshAvailable); // ssh выключен в настройках — не проверялся
        Assert.Equal(AvailabilityStatus.Online, state.AvailabilityStatus);
    }

    [Fact]
    public async Task Offline_when_no_enabled_protocol_responds()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);
        fx.Tcp.Online = _ => false;
        fx.Icmp.Online = _ => false;

        await fx.RefreshAsync(pcs);

        var state = fx.StateOf(pcs[0])!;
        Assert.False(state.IsOnline);
        Assert.Equal(AvailabilityStatus.Offline, state.AvailabilityStatus);
    }

    [Fact]
    public async Task Disabled_protocol_is_not_probed_and_stays_null()
    {
        var fx = new Fixture();
        fx.Settings.Settings.CheckIcmp = false;
        fx.Settings.Settings.CheckSmb = false;
        var pcs = fx.AddComputers(1);

        await fx.RefreshAsync(pcs);

        Assert.Empty(fx.Icmp.Calls);
        var state = fx.StateOf(pcs[0])!;
        Assert.Null(state.IcmpAvailable);
        Assert.Null(state.SmbAvailable);
        Assert.NotNull(state.RdpAvailable);
    }

    [Fact]
    public async Task Probes_use_configured_ports()
    {
        var fx = new Fixture();
        fx.Settings.Settings.RdpPort = 13389;
        fx.Settings.Settings.SmbPort = 1445;
        var pcs = fx.AddComputers(1);

        await fx.RefreshAsync(pcs);

        Assert.Contains(13389, fx.Tcp.Ports);
        Assert.Contains(1445, fx.Tcp.Ports);
    }

    [Fact]
    public async Task Checking_flag_is_set_before_probe_starts_and_cleared_after()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);
        var gate = new Gate();
        fx.Tcp.Hold = (_, ct) => gate.WaitAsync(ct);

        var refresh = fx.RefreshAsync(pcs);
        await Wait.UntilAsync(() => fx.StateOf(pcs[0])!.IsChecking, what: "флаг Checking выставился до ответа пробера");
        Assert.Equal(AvailabilityStatus.Checking, fx.StateOf(pcs[0])!.AvailabilityStatus);

        gate.Open();
        await refresh.WithTimeout();

        Assert.False(fx.StateOf(pcs[0])!.IsChecking);
        Assert.NotEqual(AvailabilityStatus.Checking, fx.StateOf(pcs[0])!.AvailabilityStatus);
    }

    [Fact]
    public async Task Not_yet_queued_computer_stays_unknown_until_its_turn()
    {
        var fx = new Fixture();
        fx.Settings.Settings.AvailabilityParallelism = 1;
        var pcs = fx.AddComputers(2);
        var gate = new Gate();
        fx.Tcp.Hold = (host, ct) => host == pcs[0].HostName ? gate.WaitAsync(ct) : Task.CompletedTask;

        var refresh = fx.RefreshAsync(pcs);
        await Wait.UntilAsync(() => fx.StateOf(pcs[0])!.IsChecking);
        await Wait.HoldsAsync(() => fx.StateOf(pcs[1])!.AvailabilityStatus == AvailabilityStatus.Unknown, 100);

        gate.Open();
        await refresh.WithTimeout();
    }

    [Fact]
    public async Task Last_checked_time_is_recorded()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);

        await fx.RefreshAsync(pcs);

        Assert.Equal(fx.Time.GetUtcNow().UtcDateTime, fx.StateOf(pcs[0])!.LastCheckedUtc);
    }

    [Fact]
    public async Task Disabled_computers_are_not_probed()
    {
        var fx = new Fixture();
        var disabled = Pc("OFF-1", id: 1, enabled: false);
        fx.Index.Add(disabled);

        await fx.RefreshAsync(new[] { disabled });

        Assert.Empty(fx.Tcp.Calls);
        Assert.Empty(fx.Icmp.Calls);
    }

    [Fact]
    public async Task Timeout_and_parallelism_come_from_settings()
    {
        var fx = new Fixture();
        fx.Settings.Settings.AvailabilityProbeTimeoutMs = 750;
        fx.Settings.Settings.AvailabilityParallelism = 3;
        var pcs = fx.AddComputers(10);

        await fx.RefreshAsync(pcs);

        Assert.All(fx.Tcp.Timeouts, t => Assert.Equal(750, t.TotalMilliseconds));
        Assert.True(fx.Tcp.MaxConcurrent <= 3 * 2); // до 2 TCP-проверок (rdp+smb) на компьютер одновременно
    }

    [Fact]
    public async Task Auto_refresh_minutes_control_cache_freshness()
    {
        var fx = new Fixture();
        fx.Settings.Settings.AvailabilityAutoRefreshMinutes = 2;
        var pcs = fx.AddComputers(1);
        await fx.RefreshAsync(pcs);
        var calls = fx.Tcp.Calls.Count;

        await fx.RefreshAsync(pcs);
        Assert.Equal(calls, fx.Tcp.Calls.Count); // в пределах интервала — кэш

        fx.Time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        await fx.RefreshAsync(pcs);
        Assert.True(fx.Tcp.Calls.Count > calls);
    }

    [Fact]
    public async Task Zero_auto_refresh_minutes_means_always_refresh()
    {
        var fx = new Fixture();
        fx.Settings.Settings.AvailabilityAutoRefreshMinutes = 0;
        var pcs = fx.AddComputers(1);
        await fx.RefreshAsync(pcs);

        await fx.RefreshAsync(pcs);

        Assert.Equal(2, fx.Icmp.Calls.Count);
    }

    [Fact]
    public async Task Force_ignores_cache()
    {
        var fx = new Fixture();
        fx.Settings.Settings.AvailabilityAutoRefreshMinutes = 10;
        var pcs = fx.AddComputers(1);
        await fx.RefreshAsync(pcs);

        await fx.RefreshAsync(pcs, force: true);

        Assert.Equal(2, fx.Icmp.Calls.Count);
    }

    [Fact]
    public async Task Probe_exception_for_one_protocol_means_that_protocol_is_unavailable_but_others_still_counted()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);
        fx.Icmp.Throwing.Add(pcs[0].HostName);
        fx.Tcp.Online = _ => true;

        await fx.RefreshAsync(pcs);

        var state = fx.StateOf(pcs[0])!;
        Assert.True(state.IsOnline);
    }

    [Fact]
    public async Task Cancellation_stops_new_probes_and_throws()
    {
        var fx = new Fixture();
        fx.Settings.Settings.AvailabilityParallelism = 2;
        var pcs = fx.AddComputers(50);
        var hold = new Gate();
        fx.Tcp.Hold = (_, ct) => hold.WaitAsync(ct);
        using var cts = new CancellationTokenSource();

        var refresh = fx.RefreshAsync(pcs, ct: cts.Token);
        await Wait.UntilAsync(() => fx.Tcp.Calls.Count >= 2);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WithTimeout());
    }

    private sealed class FakeDns(Func<string, IReadOnlyList<string>> resolve) : RemoteShadow.Core.Services.IDnsResolverService
    {
        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(resolve(host));
    }

    [Fact]
    public async Task Resolved_ip_addresses_are_stored_in_the_index()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(2);
        var service = new ComputerAvailabilityService(fx.Index, fx.Tcp, fx.Icmp, fx.Settings, fx.Time,
            new FakeDns(host => host.Contains("001", StringComparison.Ordinal) ? ["10.1.2.3"] : []));

        await service.RefreshAsync(pcs, true, CancellationToken.None);

        Assert.Equal(["10.1.2.3"], fx.StateOf(pcs[0])!.IpAddresses);
        Assert.Null(fx.StateOf(pcs[1])!.IpAddresses); // имя не разрешилось — адресов нет, это не ошибка проверки
    }

    [Fact]
    public async Task Dns_failure_does_not_break_the_availability_check()
    {
        var fx = new Fixture();
        var pcs = fx.AddComputers(1);
        fx.Tcp.Online = _ => true;
        var service = new ComputerAvailabilityService(fx.Index, fx.Tcp, fx.Icmp, fx.Settings, fx.Time,
            new FakeDns(_ => throw new InvalidOperationException("dns down")));

        await service.RefreshAsync(pcs, true, CancellationToken.None);

        Assert.True(fx.StateOf(pcs[0])!.IsOnline);
    }
}
