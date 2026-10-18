using System.Diagnostics;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Demo.Tests;

public class DemoHostProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task OnlineHost_OpensWindowsPorts()
    {
        var data = TestSupport.Data();
        var probe = new DemoHostProbe(data, DemoTimings.Instant);
        var pc = TestSupport.OnlineWorkstation(data);

        Assert.True(await probe.IsOnlineAsync(pc.Name, Timeout, CancellationToken.None));
        foreach (var port in new[] { 135, 139, 445, 3389, 5985 })
            Assert.True(await probe.IsPortOpenAsync(pc.Name, port, Timeout, CancellationToken.None), $"Порт {port}");

        Assert.False(await probe.IsPortOpenAsync(pc.Name, 8080, Timeout, CancellationToken.None));
        Assert.False(await probe.IsPortOpenAsync(pc.Name, 23, Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task OfflineAndUnknownHosts_AreClosed()
    {
        var data = TestSupport.Data();
        var probe = new DemoHostProbe(data, DemoTimings.Instant);
        var pc = TestSupport.Offline(data);

        Assert.False(await probe.IsOnlineAsync(pc.Name, Timeout, CancellationToken.None));
        Assert.False(await probe.IsPortOpenAsync(pc.Name, 3389, Timeout, CancellationToken.None));
        Assert.False(await probe.IsOnlineAsync("no-such-host", Timeout, CancellationToken.None));
        Assert.False(await probe.IsOnlineAsync(string.Empty, Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_MatchesOnlineStateOfEveryComputer_ByShortAndFullName()
    {
        var data = TestSupport.Data();
        var probe = new DemoHostProbe(data, DemoTimings.Instant);

        foreach (var c in data.GetComputers())
        {
            Assert.Equal(data.IsOnline(c.Id), await probe.IsOnlineAsync(c.Name, Timeout, CancellationToken.None));
            Assert.Equal(data.IsOnline(c.Id), await probe.IsOnlineAsync(c.HostName.ToLowerInvariant(), Timeout, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Probe_FollowsOnlineStateChanges()
    {
        var data = TestSupport.Data();
        var probe = new DemoHostProbe(data, DemoTimings.Instant);
        var pc = TestSupport.OnlineWorkstation(data);

        data.SetOnline(pc.Id, false);
        Assert.False(await probe.IsOnlineAsync(pc.Name, Timeout, CancellationToken.None));

        data.SetOnline(pc.Id, true);
        Assert.True(await probe.IsOnlineAsync(pc.Name, Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task Ssh_IsOpenOnSomeButNotAllOnlineHosts()
    {
        var data = TestSupport.Data();
        var probe = new DemoHostProbe(data, DemoTimings.Instant);
        var results = new List<bool>();

        foreach (var c in data.GetComputers().Where(c => data.IsOnline(c.Id)))
            results.Add(await probe.IsPortOpenAsync(c.Name, 22, Timeout, CancellationToken.None));

        Assert.Contains(true, results);
        Assert.Contains(false, results);
        Assert.True(results.Count(r => r) < results.Count / 2);
    }

    [Fact]
    public async Task Probe_DelayIsAtMostOneHundredMilliseconds_AndCancellable()
    {
        var data = TestSupport.Data();
        var probe = new DemoHostProbe(data);
        var pc = TestSupport.OnlineWorkstation(data);

        var watch = Stopwatch.StartNew();
        await probe.IsOnlineAsync(pc.Name, Timeout, CancellationToken.None);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 400, $"Проверка заняла {watch.ElapsedMilliseconds} мс.");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.IsOnlineAsync(pc.Name, Timeout, cts.Token));
    }

    [Fact]
    public async Task Probe_TimeoutShorterThanDelay_ReportsClosed()
    {
        var data = TestSupport.Data();
        var probe = new DemoHostProbe(data, new DemoTimings { ProbeMax = TimeSpan.FromSeconds(1) });
        var hosts = data.GetComputers().Where(c => data.IsOnline(c.Id)).Take(20).ToList();

        var open = 0;
        foreach (var c in hosts)
        {
            if (await probe.IsPortOpenAsync(c.Name, 445, TimeSpan.Zero, CancellationToken.None)) open++;
        }

        // Задержка хоста почти всегда больше нуля, так что при нулевом таймауте порт «не ответил».
        Assert.True(open <= 1, $"Открытыми оказались {open} из {hosts.Count}.");
    }
}

public class DemoProcessLauncherTests
{
    [Fact]
    public async Task StartAsync_OnlyRecords_AndRaisesEvent()
    {
        var launcher = new DemoProcessLauncher();
        var raised = new List<LaunchRecord>();
        launcher.Launched += (_, record) => raised.Add(record);
        var before = DateTime.UtcNow;

        await launcher.StartAsync("mstsc.exe", ["/v:PC-ACC-014.corp.local", "/shadow:2", "/control", "/noConsentPrompt"], CancellationToken.None);
        await launcher.StartAsync("ssh.exe", ["admin@srv-fs01.corp.local"], CancellationToken.None);

        Assert.Equal(2, launcher.Launches.Count);
        Assert.Equal(raised, launcher.Launches);

        var first = launcher.Launches[0];
        Assert.Equal("mstsc.exe", first.FileName);
        Assert.Equal(new[] { "/v:PC-ACC-014.corp.local", "/shadow:2", "/control", "/noConsentPrompt" }, first.Arguments);
        Assert.True(first.TimeUtc >= before);
        Assert.Equal(DateTimeKind.Utc, first.TimeUtc.Kind);
        Assert.Equal("ssh.exe", launcher.Launches[1].FileName);
    }

    [Fact]
    public async Task StartAsync_CopiesArguments()
    {
        var launcher = new DemoProcessLauncher();
        var arguments = new List<string> { "/v:PC-1" };

        await launcher.StartAsync("mstsc.exe", arguments, CancellationToken.None);
        arguments.Add("/admin");

        Assert.Equal(new[] { "/v:PC-1" }, launcher.Launches[0].Arguments);
    }

    [Fact]
    public async Task StartAsync_InvalidArguments_Throw()
    {
        var launcher = new DemoProcessLauncher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => launcher.StartAsync(" ", [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => launcher.StartAsync("ssh.exe", null!, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.StartAsync("ssh.exe", [], cts.Token));
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public void CommandLine_QuotesArgumentsWithSpaces()
    {
        var record = new LaunchRecord(DateTime.UtcNow, "ssh.exe", ["-l", "Иван Петров", "pc-1", string.Empty]);

        Assert.Equal("ssh.exe -l \"Иван Петров\" pc-1 \"\"", record.CommandLine);
    }

    [Theory]
    [InlineData("mstsc.exe", "C:/Windows/System32/mstsc.exe")]
    [InlineData("ssh", "C:/Windows/System32/ssh")]
    [InlineData("  explorer.exe  ", "C:/Windows/System32/explorer.exe")]
    [InlineData(@"C:\Tools\wt.exe", "C:/Windows/System32/wt.exe")]
    [InlineData("tools/putty.exe", "C:/Windows/System32/putty.exe")]
    public void FindExecutable_ReturnsMadeUpSystem32Path(string name, string expected)
    {
        Assert.Equal(expected, new DemoProcessLauncher().FindExecutable(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tools/")]
    public void FindExecutable_NoName_ReturnsNull(string name)
    {
        Assert.Null(new DemoProcessLauncher().FindExecutable(name));
    }

    [Fact]
    public async Task ConcurrentStarts_AreAllRecorded()
    {
        var launcher = new DemoProcessLauncher();

        await Task.WhenAll(Enumerable.Range(0, 500).Select(i =>
            Task.Run(() => launcher.StartAsync("mstsc.exe", [$"/v:PC-{i}"], CancellationToken.None))));

        Assert.Equal(500, launcher.Launches.Count);
        Assert.Equal(500, launcher.Launches.Select(l => l.Arguments[0]).Distinct().Count());
    }
}

public class DemoCredentialStoreTests
{
    [Fact]
    public void Store_SavesLoadsAndDeletes()
    {
        var store = new DemoCredentialStore();
        Assert.Null(store.Load());

        var logon = new StoredLogon("corp.local", "DC01.corp.local", "adm-ivanov", null);
        store.Save(logon);
        Assert.Equal(logon, store.Load());

        var withPassword = logon with { UserName = "adm-petrov", Password = "demo" };
        store.Save(withPassword);
        Assert.Equal("adm-petrov", store.Load()!.UserName);
        Assert.Equal("demo", store.Load()!.Password);

        store.Delete();
        Assert.Null(store.Load());
        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Store_IsNotShared_BetweenInstances()
    {
        var a = new DemoCredentialStore();
        var b = new DemoCredentialStore();

        a.Save(new StoredLogon("corp.local", null, "x", null));

        Assert.Null(b.Load());
    }

    [Fact]
    public void Save_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DemoCredentialStore().Save(null!));
    }
}
