using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Demo.Tests;

public class DemoDiagnosticsServiceTests
{
    private static (DemoData Data, DemoDiagnosticsService Service) Create(int count = 1500, int seed = 1)
    {
        var data = TestSupport.Data(count, seed);
        return (data, new DemoDiagnosticsService(data, DemoTimings.Instant));
    }

    /// <summary>Компьютер «в сети», с DNS-именем, которому по имени выпадает нужный сценарий.</summary>
    private static ComputerInfo OnlineWith(DemoData data, DemoDiagnosticScenario scenario) =>
        data.GetComputers().First(c =>
            c.DnsName is not null && data.IsOnline(c.Id) && DemoDiagnosticsService.GetScenario(c.Name) == scenario);

    private static DiagnosticItem Item(DiagnosticReport report, string id) => report.Items.Single(i => i.Id == id);

    [Fact]
    public async Task Report_IsDeterministicPerComputer()
    {
        var (data, service) = Create(230);

        foreach (var c in data.GetComputers().Take(60))
        {
            var a = await service.RunAsync(c, CancellationToken.None);
            var b = await service.RunAsync(c, CancellationToken.None);

            Assert.Equal(a.Verdict, b.Verdict);
            Assert.Equal(a.Items, b.Items);
            Assert.Equal(c.HostName, a.Host);
            Assert.Equal(DateTimeKind.Utc, a.CreatedUtc.Kind);
        }

        // Второй экземпляр сервиса говорит то же самое.
        var other = new DemoDiagnosticsService(data, DemoTimings.Instant);
        var sample = data.GetComputers()[7];
        Assert.Equal((await service.RunAsync(sample, CancellationToken.None)).Items, (await other.RunAsync(sample, CancellationToken.None)).Items);
    }

    [Fact]
    public void Scenarios_AreSpreadAcrossComputers()
    {
        var (data, _) = Create(1500);
        var counts = data.GetComputers()
            .GroupBy(c => DemoDiagnosticsService.GetScenario(c.Name))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var scenario in Enum.GetValues<DemoDiagnosticScenario>())
            Assert.True(counts.GetValueOrDefault(scenario) > 0, $"Нет компьютеров со сценарием {scenario}.");

        // Больше половины — без проблем, но проблемных достаточно, чтобы их показать.
        Assert.InRange(counts[DemoDiagnosticScenario.Healthy], 550, 950);
        Assert.Equal(DemoDiagnosticsService.GetScenario("PC-ACC-014"), DemoDiagnosticsService.GetScenario("pc-acc-014"));
    }

    [Fact]
    public async Task Healthy_AllOk_WithAllExpectedChecksInOrder()
    {
        var (data, service) = Create();
        var pc = OnlineWith(data, DemoDiagnosticScenario.Healthy);

        var report = await service.RunAsync(pc, CancellationToken.None);

        Assert.Equal(
            new[] { "dns", "port-135", "port-445", "port-3389", "port-5985", "termservice", "shadow-policy", "shadow-firewall", "allow-remote-rpc" },
            report.Items.Select(i => i.Id));
        Assert.All(report.Items, i => Assert.Equal(DiagnosticStatus.Ok, i.Status));
        Assert.All(report.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Title)));
        Assert.All(report.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Detail)));
        Assert.All(report.Items, i => Assert.Null(i.FixId));
        Assert.False(report.HasProblems);
        Assert.StartsWith("Всё в порядке", report.Verdict);
        Assert.Contains(pc.HostName, Item(report, "dns").Detail);
    }

    [Fact]
    public async Task ShadowPolicyOne_IsOkButExplainsConsentInHint()
    {
        var (data, service) = Create();
        var pc = OnlineWith(data, DemoDiagnosticScenario.ShadowNeedsConsent);

        var report = await service.RunAsync(pc, CancellationToken.None);

        var policy = Item(report, "shadow-policy");
        Assert.Equal(DiagnosticStatus.Ok, policy.Status);
        Assert.Contains("Shadow = 1", policy.Detail);
        Assert.False(string.IsNullOrWhiteSpace(policy.Hint));
        Assert.Contains("запрос", policy.Hint);
        Assert.Contains("2", policy.Hint);
        Assert.Null(policy.FixId);
        Assert.False(report.HasProblems);
        Assert.StartsWith("Всё в порядке", report.Verdict);
    }

    [Fact]
    public async Task ShadowPolicyZero_BlocksShadow_WithoutFixButton()
    {
        var (data, service) = Create();
        var pc = OnlineWith(data, DemoDiagnosticScenario.ShadowForbidden);

        var report = await service.RunAsync(pc, CancellationToken.None);

        var policy = Item(report, "shadow-policy");
        Assert.Equal(DiagnosticStatus.Fail, policy.Status);
        Assert.Contains("Shadow = 0", policy.Detail);
        Assert.Contains("групповую политику", policy.Hint);
        Assert.Null(policy.FixId);
        Assert.True(report.HasProblems);
        Assert.Equal("Shadow заблокирован политикой", report.Verdict);
    }

    [Fact]
    public async Task OfflineComputer_ShowsClosedPort445_AndSkipsWinRmChecks()
    {
        var (data, service) = Create();
        var pc = data.GetComputers().First(c => c.DnsName is not null && !data.IsOnline(c.Id));

        var report = await service.RunAsync(pc, CancellationToken.None);

        var port = Item(report, "port-445");
        Assert.Equal(DiagnosticStatus.Fail, port.Status);
        Assert.False(string.IsNullOrWhiteSpace(port.Hint));
        Assert.Equal("Shadow заблокирован: порт 445 закрыт", report.Verdict);
        Assert.Equal(DiagnosticStatus.Ok, Item(report, "dns").Status);

        foreach (var id in new[] { "termservice", "shadow-policy", "shadow-firewall", "allow-remote-rpc" })
        {
            var item = Item(report, id);
            Assert.Equal(DiagnosticStatus.Unknown, item.Status);
            Assert.StartsWith("Не удалось проверить", item.Detail);
        }
    }

    [Fact]
    public async Task OnlineComputer_NeverReportsClosedPort445()
    {
        var (data, service) = Create(230);

        foreach (var c in data.GetComputers().Where(c => c.DnsName is not null && data.IsOnline(c.Id)))
        {
            var report = await service.RunAsync(c, CancellationToken.None);
            Assert.Equal(DiagnosticStatus.Ok, Item(report, "port-445").Status);
        }
    }

    [Fact]
    public async Task ComputerWithoutDnsName_FailsOnDns_AndSkipsEverythingElse()
    {
        var (data, service) = Create();
        var pc = data.GetComputers().First(c => c.DnsName is null);

        var report = await service.RunAsync(pc, CancellationToken.None);

        var dns = Item(report, "dns");
        Assert.Equal(DiagnosticStatus.Fail, dns.Status);
        Assert.Contains(pc.Name, dns.Detail);
        Assert.Equal("Shadow недоступен: имя компьютера не разрешается в DNS", report.Verdict);
        Assert.All(report.Items.Where(i => i.Id != "dns"), i => Assert.Equal(DiagnosticStatus.Unknown, i.Status));
        Assert.Equal(9, report.Items.Count);
    }

    [Fact]
    public async Task WinRmUnavailable_LeavesCimChecksUnknown_WithHint()
    {
        var (data, service) = Create();
        var pc = OnlineWith(data, DemoDiagnosticScenario.WinRmUnavailable);

        var report = await service.RunAsync(pc, CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Warning, Item(report, "port-5985").Status);
        Assert.Equal(DiagnosticStatus.Ok, Item(report, "port-445").Status);
        foreach (var id in new[] { "termservice", "shadow-policy", "shadow-firewall", "allow-remote-rpc" })
        {
            var item = Item(report, id);
            Assert.Equal(DiagnosticStatus.Unknown, item.Status);
            Assert.Contains("WinRM", item.Detail);
            Assert.Contains("WinRM", item.Hint);
        }

        Assert.StartsWith("Явных причин блокировки shadow не найдено, но часть проверок", report.Verdict);
    }

    [Fact]
    public async Task StoppedTermService_HasFixId_AndFixStartsIt()
    {
        var (data, service) = Create();
        var pc = OnlineWith(data, DemoDiagnosticScenario.TermServiceStopped);

        var before = await service.RunAsync(pc, CancellationToken.None);

        var stopped = Item(before, "termservice");
        Assert.Equal(DiagnosticStatus.Fail, stopped.Status);
        Assert.Equal("start-termservice", stopped.FixId);
        Assert.Equal(DemoDiagnosticsService.StartTermServiceFixId, stopped.FixId);
        Assert.True(before.HasProblems);
        Assert.Contains("TermService", before.Verdict);
        Assert.Equal(1, before.Items.Count(i => i.FixId is not null));

        await service.FixAsync(pc, stopped.FixId!, CancellationToken.None);

        var after = await service.RunAsync(pc, CancellationToken.None);
        var running = Item(after, "termservice");
        Assert.Equal(DiagnosticStatus.Ok, running.Status);
        Assert.Null(running.FixId);
        Assert.False(after.HasProblems);
        Assert.StartsWith("Всё в порядке", after.Verdict);
    }

    [Fact]
    public async Task Fix_AffectsOnlyThatComputer()
    {
        var (data, service) = Create();
        var stoppedOnes = data.GetComputers()
            .Where(c => c.DnsName is not null && data.IsOnline(c.Id) && DemoDiagnosticsService.GetScenario(c.Name) == DemoDiagnosticScenario.TermServiceStopped)
            .Take(2)
            .ToList();
        Assert.Equal(2, stoppedOnes.Count);

        await service.FixAsync(stoppedOnes[0], DemoDiagnosticsService.StartTermServiceFixId, CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Ok, Item(await service.RunAsync(stoppedOnes[0], CancellationToken.None), "termservice").Status);
        Assert.Equal(DiagnosticStatus.Fail, Item(await service.RunAsync(stoppedOnes[1], CancellationToken.None), "termservice").Status);
    }

    [Fact]
    public async Task Fix_UnknownId_IsInvalidInput()
    {
        var (data, service) = Create();
        var pc = data.GetComputers()[0];

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.FixAsync(pc, "format-c", CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
    }

    [Fact]
    public async Task Fix_OfflineComputer_IsUnreachable()
    {
        var (data, service) = Create();
        var pc = TestSupport.Offline(data);

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => service.FixAsync(pc, DemoDiagnosticsService.StartTermServiceFixId, CancellationToken.None));

        Assert.Equal(OperationFailure.Unreachable, ex.Failure);
        Assert.Contains(pc.Name, ex.Message);
    }

    [Fact]
    public async Task ComputerUnknownToDirectory_StillGetsAReport()
    {
        var (_, service) = Create(230);
        var stranger = new ComputerInfo
        {
            Id = Guid.NewGuid(),
            Name = "PC-NEW-777",
            DnsName = "PC-NEW-777.corp.local",
            DistinguishedName = "CN=PC-NEW-777,OU=Склад,DC=corp,DC=local",
        };

        var report = await service.RunAsync(stranger, CancellationToken.None);

        Assert.Equal("PC-NEW-777.corp.local", report.Host);
        Assert.NotEmpty(report.Items);
        Assert.False(string.IsNullOrWhiteSpace(report.Verdict));
    }

    [Fact]
    public async Task EveryComputer_GetsConsistentReport()
    {
        var (data, service) = Create(230);

        foreach (var c in data.GetComputers())
        {
            var report = await service.RunAsync(c, CancellationToken.None);

            Assert.Equal(9, report.Items.Count);
            Assert.Equal(report.Items.Count, report.Items.Select(i => i.Id).Distinct().Count());
            Assert.False(string.IsNullOrWhiteSpace(report.Verdict));
            Assert.All(report.Items.Where(i => i.Status is DiagnosticStatus.Fail or DiagnosticStatus.Warning), i => Assert.False(string.IsNullOrWhiteSpace(i.Hint)));
            Assert.All(report.Items.Where(i => i.FixId is not null), i => Assert.Equal("termservice", i.Id));
        }
    }

    [Fact]
    public async Task Run_Cancelled_Throws()
    {
        var data = TestSupport.Data();
        var service = new DemoDiagnosticsService(data, new DemoTimings { DiagnosticsMin = TimeSpan.FromSeconds(30), DiagnosticsMax = TimeSpan.FromSeconds(30) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(data.GetComputers()[0], cts.Token));
    }
}
