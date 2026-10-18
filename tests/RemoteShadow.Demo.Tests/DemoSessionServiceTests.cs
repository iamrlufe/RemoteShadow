using System.Diagnostics;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Demo.Tests;

public class DemoSessionServiceTests
{
    private static (DemoData Data, DemoSessionService Service) Create(DemoTimings? timings = null, int count = 230, int seed = 1)
    {
        var data = TestSupport.Data(count, seed);
        return (data, new DemoSessionService(data, timings ?? DemoTimings.Instant));
    }

    private static ComputerInfo Denied(DemoData data) =>
        data.GetComputers().First(c => data.IsAccessDenied(c.Id));

    // ---------- опрос ----------

    [Fact]
    public async Task GetSessions_OnlineHost_ReturnsSessionsFromData()
    {
        var (data, service) = Create();
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);

        var result = await service.GetSessionsAsync(pc.Name, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(HostQueryStatus.Ok, result.Status);
        Assert.Equal(data.GetSessions(pc.Id), result.Sessions);
        Assert.Contains(result.Sessions, s => s.State == SessionState.Active && s.HasUser);
        Assert.NotNull(result.QueriedAt);
    }

    [Fact]
    public async Task GetSessions_AcceptsShortAndFullNamesInAnyCase()
    {
        var (data, service) = Create();
        var pc = data.GetComputers().First(c => c.DnsName is not null && data.IsOnline(c.Id) && !data.IsAccessDenied(c.Id));

        var shortName = await service.GetSessionsAsync(pc.Name.ToLowerInvariant(), CancellationToken.None);
        var fullName = await service.GetSessionsAsync(pc.DnsName!.ToUpperInvariant(), CancellationToken.None);

        Assert.True(shortName.IsOk);
        Assert.Equal(shortName.Sessions, fullName.Sessions);
    }

    [Fact]
    public async Task GetSessions_OfflineHost_IsUnreachable()
    {
        var (data, service) = Create();
        var pc = TestSupport.Offline(data);

        var result = await service.GetSessionsAsync(pc.Name, CancellationToken.None);

        Assert.Equal(HostQueryStatus.Unreachable, result.Status);
        Assert.False(result.IsOk);
        Assert.Empty(result.Sessions);
        Assert.Contains(pc.Name, result.Message);
        Assert.EndsWith(")", result.Message!.TrimEnd('.', ' '));
    }

    [Fact]
    public async Task GetSessions_UnknownHost_IsUnreachable()
    {
        var (_, service) = Create();

        var result = await service.GetSessionsAsync("no-such-host.corp.local", CancellationToken.None);

        Assert.Equal(HostQueryStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task GetSessions_HostWithoutRights_IsAccessDenied()
    {
        var (data, service) = Create();
        var pc = Denied(data);

        var result = await service.GetSessionsAsync(pc.HostName, CancellationToken.None);

        Assert.Equal(HostQueryStatus.AccessDenied, result.Status);
        Assert.Empty(result.Sessions);
        Assert.Contains(pc.Name, result.Message);
    }

    [Fact]
    public async Task GetSessions_OfflineHost_WaitsAboutEightHundredMilliseconds()
    {
        var (data, service) = Create(DemoTimings.Default);
        var pc = TestSupport.Offline(data);

        var watch = Stopwatch.StartNew();
        var result = await service.GetSessionsAsync(pc.Name, CancellationToken.None);
        watch.Stop();

        Assert.Equal(HostQueryStatus.Unreachable, result.Status);
        Assert.True(watch.ElapsedMilliseconds >= 750, $"Ответ пришёл за {watch.ElapsedMilliseconds} мс, ожидалось около 800.");
    }

    [Fact]
    public async Task GetSessions_OnlineHost_WaitsItsDeterministicDelay()
    {
        var (data, service) = Create(DemoTimings.Default);
        var pc = data.GetComputers()
            .Where(c => data.IsOnline(c.Id) && !data.IsAccessDenied(c.Id))
            .MinBy(c => service.GetQueryDelay(c.Name))!;
        var expected = service.GetQueryDelay(pc.Name);

        var watch = Stopwatch.StartNew();
        await service.GetSessionsAsync(pc.Name, CancellationToken.None);
        watch.Stop();

        Assert.InRange(expected.TotalMilliseconds, 150, 220);
        Assert.True(watch.Elapsed >= expected - TimeSpan.FromMilliseconds(30), $"Опрос занял {watch.ElapsedMilliseconds} мс при задержке {expected.TotalMilliseconds} мс.");
    }

    [Fact]
    public void QueryDelay_IsBetween150And600_AndDependsOnlyOnHostName()
    {
        var (data, service) = Create(DemoTimings.Default);
        var computers = data.GetComputers();

        var delays = computers.Select(c => service.GetQueryDelay(c.Name)).ToList();

        Assert.All(delays, d => Assert.InRange(d.TotalMilliseconds, 150, 600));
        Assert.True(delays.Distinct().Count() > 30, "Задержки должны быть разными у разных хостов.");
        Assert.Contains(delays, d => d < TimeSpan.FromMilliseconds(250));
        Assert.Contains(delays, d => d > TimeSpan.FromMilliseconds(500));

        foreach (var c in computers.Take(40))
        {
            Assert.Equal(service.GetQueryDelay(c.Name), service.GetQueryDelay(c.Name.ToLowerInvariant()));
            if (c.DnsName is not null) Assert.Equal(service.GetQueryDelay(c.Name), service.GetQueryDelay(c.DnsName));
        }

        // Второй экземпляр на тех же данных даёт те же задержки: нет зависимости от процесса или порядка вызовов.
        var other = new DemoSessionService(data, DemoTimings.Default);
        Assert.Equal(delays, computers.Select(c => other.GetQueryDelay(c.Name)));
    }

    [Fact]
    public async Task GetSessions_Cancelled_Throws()
    {
        var (data, service) = Create(DemoTimings.Default);
        var pc = TestSupport.Offline(data);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetSessionsAsync(pc.Name, cts.Token));

        Assert.True(watch.ElapsedMilliseconds < 700, "Отмена должна прерывать ожидание.");
    }

    [Fact]
    public async Task GetSessions_SeesServerWithSeveralRdpSessions()
    {
        var (data, service) = Create();
        var server = data.GetComputers().First(c => TestSupport.IsServer(c) && data.IsOnline(c.Id) && data.GetSessions(c.Id).Count >= 2);

        var result = await service.GetSessionsAsync(server.HostName, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.True(result.Sessions.Count >= 2);
    }

    // ---------- действия над сессиями ----------

    [Fact]
    public async Task Logoff_RemovesSession()
    {
        var (data, service) = Create();
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);
        var session = data.GetSessions(pc.Id).First(s => s.State == SessionState.Active && s.HasUser);

        await service.LogoffAsync(pc.Name, session.Id, CancellationToken.None);

        var result = await service.GetSessionsAsync(pc.Name, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.DoesNotContain(result.Sessions, s => s.Id == session.Id);
        Assert.Equal(DemoActionKind.Logoff, Assert.Single(service.Actions).Kind);
    }

    [Fact]
    public async Task Logoff_TwiceOrUnknownSession_IsNotFound()
    {
        var (data, service) = Create();
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);
        var session = data.GetSessions(pc.Id).First(s => s.State == SessionState.Active);

        await service.LogoffAsync(pc.Name, session.Id, CancellationToken.None);
        var again = await Assert.ThrowsAsync<OperationFailedException>(() => service.LogoffAsync(pc.Name, session.Id, CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<OperationFailedException>(() => service.DisconnectAsync(pc.Name, 9999, CancellationToken.None));

        Assert.Equal(OperationFailure.NotFound, again.Failure);
        Assert.Equal(OperationFailure.NotFound, unknown.Failure);
        Assert.Single(service.Actions);
    }

    [Fact]
    public async Task Disconnect_MakesSessionDisconnected_AndShadowImpossible()
    {
        var (data, service) = Create();
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);
        var session = data.GetSessions(pc.Id).First(s => s.State == SessionState.Active && s.HasUser);
        Assert.True(session.CanShadow);

        await service.DisconnectAsync(pc.Name, session.Id, CancellationToken.None);

        var after = (await service.GetSessionsAsync(pc.Name, CancellationToken.None)).Sessions.Single(s => s.Id == session.Id);
        Assert.Equal(SessionState.Disconnected, after.State);
        Assert.Equal(session.UserName, after.UserName);
        Assert.False(after.CanShadow);
        Assert.Equal(DemoActionKind.Disconnect, Assert.Single(service.Actions).Kind);
    }

    [Fact]
    public async Task SendMessage_LeavesSessionsAndIsLogged()
    {
        var (data, service) = Create();
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);
        var session = data.GetSessions(pc.Id).First(s => s.State == SessionState.Active);
        var before = data.GetSessions(pc.Id);

        await service.SendMessageAsync(pc.Name, session.Id, "ИТ-отдел", "Через 10 минут перезагрузка", CancellationToken.None);

        Assert.Equal(before, data.GetSessions(pc.Id));
        var action = Assert.Single(service.Actions);
        Assert.Equal(DemoActionKind.SendMessage, action.Kind);
        Assert.Equal(pc.Name, action.Host);
        Assert.Equal(session.Id, action.SessionId);
        Assert.Contains("ИТ-отдел", action.Details);
        Assert.Contains("перезагрузка", action.Details);
    }

    [Fact]
    public async Task Actions_RaiseEventAndKeepOrder()
    {
        var (data, service) = Create();
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);
        var session = data.GetSessions(pc.Id).First(s => s.State == SessionState.Active);
        var seen = new List<DemoAction>();
        service.ActionPerformed += (_, action) => seen.Add(action);

        await service.SendMessageAsync(pc.HostName, session.Id, "Тема", "Текст", CancellationToken.None);
        await service.DisconnectAsync(pc.HostName, session.Id, CancellationToken.None);
        await service.LogoffAsync(pc.HostName, session.Id, CancellationToken.None);

        Assert.Equal(
            new[] { DemoActionKind.SendMessage, DemoActionKind.Disconnect, DemoActionKind.Logoff },
            seen.Select(a => a.Kind));
        Assert.Equal(seen, service.Actions);
        Assert.All(seen, a => Assert.Equal(pc.Name, a.Host));
        Assert.All(seen, a => Assert.Equal(DateTimeKind.Utc, a.TimeUtc.Kind));
    }

    [Fact]
    public async Task Actions_OnOfflineHost_AreUnreachable_AndNotLogged()
    {
        var (data, service) = Create();
        var pc = TestSupport.Offline(data);

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.LogoffAsync(pc.Name, 1, CancellationToken.None));
        var ex2 = await Assert.ThrowsAsync<OperationFailedException>(() => service.ShutdownAsync(pc.Name, true, TimeSpan.Zero, null, CancellationToken.None));

        Assert.Equal(OperationFailure.Unreachable, ex.Failure);
        Assert.Equal(OperationFailure.Unreachable, ex2.Failure);
        Assert.Empty(service.Actions);
    }

    [Fact]
    public async Task Actions_OnHostWithoutRights_AreAccessDenied()
    {
        var (data, service) = Create();
        var pc = Denied(data);
        var session = data.GetSessions(pc.Id).First();

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.LogoffAsync(pc.Name, session.Id, CancellationToken.None));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains(pc.Name, ex.Message);
        Assert.Contains(session, data.GetSessions(pc.Id));
        Assert.Empty(service.Actions);
    }

    [Fact]
    public async Task Actions_OnUnknownHost_AreNotFound()
    {
        var (_, service) = Create();

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.DisconnectAsync("no-such-host", 1, CancellationToken.None));

        Assert.Equal(OperationFailure.NotFound, ex.Failure);
    }

    [Fact]
    public async Task Actions_Cancelled_Throw_AndChangeNothing()
    {
        var (data, service) = Create(DemoTimings.Default);
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);
        var before = data.GetSessions(pc.Id);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LogoffAsync(pc.Name, before[0].Id, cts.Token));

        Assert.Equal(before, data.GetSessions(pc.Id));
        Assert.Empty(service.Actions);
    }

    // ---------- перезагрузка и выключение ----------

    [Fact]
    public void Default_ShutdownReturnDelay_IsTwentySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), DemoTimings.Default.ShutdownReturnDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(800), DemoTimings.Default.UnreachableDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(150), DemoTimings.Default.SessionQueryMin);
        Assert.Equal(TimeSpan.FromMilliseconds(600), DemoTimings.Default.SessionQueryMax);
    }

    [Theory]
    [InlineData(true, DemoActionKind.Reboot)]
    [InlineData(false, DemoActionKind.Shutdown)]
    public async Task Shutdown_TakesComputerOfflineAndBringsItBack(bool reboot, DemoActionKind kind)
    {
        var (data, service) = Create(DemoTimings.Instant with { ShutdownReturnDelay = TimeSpan.FromSeconds(1.5) });
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);

        await service.ShutdownAsync(pc.Name, reboot, TimeSpan.FromSeconds(30), "Обновление системы", CancellationToken.None);

        Assert.False(data.IsOnline(pc.Id));
        Assert.Empty(data.GetSessions(pc.Id));
        Assert.Equal(HostQueryStatus.Unreachable, (await service.GetSessionsAsync(pc.Name, CancellationToken.None)).Status);

        var action = Assert.Single(service.Actions);
        Assert.Equal(kind, action.Kind);
        Assert.Null(action.SessionId);
        Assert.Contains("30", action.Details);
        Assert.Contains("Обновление системы", action.Details);

        // Здесь компьютер возвращается через 1,5 секунды; с настоящими таймингами — через 20 секунд.
        Assert.True(await TestSupport.WaitUntilAsync(() => data.IsOnline(pc.Id), TimeSpan.FromSeconds(10)), "Компьютер не вернулся в сеть.");
        Assert.True((await service.GetSessionsAsync(pc.Name, CancellationToken.None)).IsOk);
    }

    [Fact]
    public async Task Shutdown_WithDefaultTimings_KeepsComputerOfflineForSeconds()
    {
        var (data, service) = Create(new DemoTimings { ActionDelay = TimeSpan.Zero });
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);

        await service.ShutdownAsync(pc.Name, true, TimeSpan.Zero, null, CancellationToken.None);
        await Task.Delay(1500);

        Assert.False(data.IsOnline(pc.Id), "Через 1,5 секунды компьютер ещё должен быть «не в сети» (возврат через 20 с).");
    }

    [Fact]
    public async Task Shutdown_ComputerRemovedMeanwhile_DoesNotBreak()
    {
        var (data, service) = Create();
        var directory = new DemoComputerDirectory(data, DemoTimings.Instant);
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);

        await service.ShutdownAsync(pc.Name, false, TimeSpan.Zero, null, CancellationToken.None);
        directory.Remove(pc.Id);
        await Task.Delay(600);

        Assert.Null(data.FindComputer(pc.Id));
        Assert.False(data.IsOnline(pc.Id));
    }

    [Fact]
    public async Task ConcurrentActions_AreAllLogged()
    {
        var (data, service) = Create();
        var targets = data.GetComputers()
            .Where(c => !TestSupport.IsServer(c) && data.IsOnline(c.Id) && !data.IsAccessDenied(c.Id))
            .Select(c => (Computer: c, Session: data.GetSessions(c.Id).FirstOrDefault(s => s.HasUser)))
            .Where(x => x.Session is not null)
            .Take(60)
            .ToList();

        await Task.WhenAll(targets.Select(t => service.SendMessageAsync(t.Computer.Name, t.Session!.Id, "T", "M", CancellationToken.None)));

        Assert.Equal(targets.Count, service.Actions.Count);
        Assert.Equal(targets.Count, service.Actions.Select(a => a.Host).Distinct().Count());
    }
}
