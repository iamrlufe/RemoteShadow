using System.Net;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts.Tests;

public class CimDiagnosticsServiceTests
{
    private sealed class FakeCim : ICimRemote
    {
        public ServiceState? Service { get; set; } = new("Running", "Auto");
        public uint? ShadowPolicy { get; set; }
        public uint? AllowRemoteRpc { get; set; } = 1;
        public bool? FirewallEnabled { get; set; } = true;
        public uint StartServiceResult { get; set; }
        public Exception? ServiceError { get; set; }
        public Exception? RegistryError { get; set; }
        public Exception? FirewallError { get; set; }
        public Action? OnGetService { get; set; }
        public List<string> Calls { get; } = [];
        public bool Disposed { get; private set; }

        public ServiceState? GetService(string serviceName)
        {
            Calls.Add($"service {serviceName}");
            OnGetService?.Invoke();
            if (ServiceError is not null) throw ServiceError;
            return Service;
        }

        public uint StartService(string serviceName)
        {
            Calls.Add($"start {serviceName}");
            if (ServiceError is not null) throw ServiceError;
            return StartServiceResult;
        }

        public uint? ReadHklmDword(string subKey, string valueName)
        {
            Calls.Add($"reg {valueName}");
            if (RegistryError is not null) throw RegistryError;
            return valueName switch
            {
                "Shadow" => ShadowPolicy,
                "AllowRemoteRPC" => AllowRemoteRpc,
                _ => null,
            };
        }

        public bool? IsFirewallRuleEnabled(string ruleName, string displayName)
        {
            Calls.Add($"firewall {ruleName}");
            if (FirewallError is not null) throw FirewallError;
            return FirewallEnabled;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class Harness
    {
        public FakeCim Cim { get; } = new();
        public FakeConnection Connection { get; init; } = new();
        public int FactoryCalls;
        public string? FactoryHost;
        public AccountCredential? FactoryCredential;
        public Exception? FactoryError { get; set; }
        public int[] OpenPorts { get; set; } = [135, 445, 3389, 5985];
        public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; set; } = (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") });
        public TimeSpan CimDeadline { get; set; } = TimeSpan.FromSeconds(10);

        public CimDiagnosticsService Build() => new(
            TestConnections.LoggedIn(Connection),
            new FakeProbe(OpenPorts),
            (host, credential) =>
            {
                Interlocked.Increment(ref FactoryCalls);
                FactoryHost = host;
                FactoryCredential = credential;
                if (FactoryError is not null) throw FactoryError;
                return Cim;
            },
            Resolve,
            CimDeadline);
    }

    private static DiagnosticItem Get(DiagnosticReport report, string id) => report.Items.Single(i => i.Id == id);

    [Fact]
    public async Task Healthy_computer_all_ok_with_full_item_list_in_order()
    {
        var h = new Harness();

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticIds.DisplayOrder, report.Items.Select(i => i.Id).ToArray());
        Assert.All(report.Items, i => Assert.Equal(DiagnosticStatus.Ok, i.Status));
        Assert.StartsWith("Всё в порядке", report.Verdict);
        Assert.False(report.HasProblems);
        Assert.Equal("pc-acc-014.corp.local", report.Host);
        Assert.Equal("pc-acc-014.corp.local", h.FactoryHost);
        Assert.True(h.Cim.Disposed);
        Assert.Equal(DateTimeKind.Utc, report.CreatedUtc.Kind);
    }

    [Fact]
    public async Task Uses_dns_name_for_connection_and_short_name_otherwise()
    {
        var h = new Harness();

        var report = await h.Build().RunAsync(TestComputers.Pc("PC-1", dns: null), CancellationToken.None);

        Assert.Equal("PC-1", report.Host);
        Assert.Equal("PC-1", h.FactoryHost);
    }

    [Fact]
    public async Task Dns_failure_skips_everything_else_as_unknown()
    {
        var h = new Harness { Resolve = (_, _) => throw new System.Net.Sockets.SocketException(11001) };

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Fail, Get(report, DiagnosticIds.Dns).Status);
        Assert.All(report.Items.Where(i => i.Id != DiagnosticIds.Dns), i => Assert.Equal(DiagnosticStatus.Unknown, i.Status));
        Assert.Equal(DiagnosticIds.DisplayOrder.Count, report.Items.Count);
        Assert.Contains("DNS", report.Verdict);
        Assert.Equal(0, h.FactoryCalls);
    }

    [Fact]
    public async Task Dns_with_no_addresses_is_fail()
    {
        var h = new Harness { Resolve = (_, _) => Task.FromResult(Array.Empty<IPAddress>()) };

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Fail, Get(report, DiagnosticIds.Dns).Status);
    }

    [Fact]
    public async Task Ip_address_skips_name_resolution()
    {
        var resolveCalls = 0;
        var h = new Harness { Resolve = (_, _) => { resolveCalls++; return Task.FromResult(Array.Empty<IPAddress>()); } };

        var report = await h.Build().RunAsync(TestComputers.Pc("PC", dns: "10.20.30.40"), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.Dns).Status);
        Assert.Equal(0, resolveCalls);
    }

    [Fact]
    public async Task Caller_cancellation_during_dns_is_propagated()
    {
        var h = new Harness
        {
            Resolve = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            },
        };
        using var cts = new CancellationTokenSource();
        var service = h.Build();
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(TestComputers.Pc(), cts.Token));
    }

    [Fact]
    public async Task Port_445_closed_is_fail_and_drives_verdict()
    {
        var h = new Harness { OpenPorts = [135, 3389, 5985] };

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Fail, Get(report, DiagnosticIds.Port445).Status);
        Assert.Equal("Shadow заблокирован: порт 445 закрыт", report.Verdict);
        Assert.True(report.HasProblems);
    }

    [Fact]
    public async Task Winrm_closed_marks_cim_checks_unknown_and_never_opens_a_cim_session()
    {
        var h = new Harness { OpenPorts = [135, 445, 3389] };

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Warning, Get(report, DiagnosticIds.Port5985).Status);
        foreach (var id in DiagnosticIds.CimChecks)
        {
            var item = Get(report, id);
            Assert.Equal(DiagnosticStatus.Unknown, item.Status);
            Assert.Contains("WinRM (5985) недоступен", item.Detail);
        }

        Assert.Equal(0, h.FactoryCalls);
        Assert.Contains("часть проверок выполнить не удалось", report.Verdict);
    }

    [Fact]
    public async Task Stopped_termservice_offers_fix_and_drives_verdict()
    {
        var h = new Harness();
        h.Cim.Service = new ServiceState("Stopped", "Manual");

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        var item = Get(report, DiagnosticIds.TermService);
        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Equal("start-termservice", item.FixId);
        Assert.Contains("TermService", report.Verdict);
    }

    [Fact]
    public async Task Policy_zero_blocks_shadow_without_fix()
    {
        var h = new Harness();
        h.Cim.ShadowPolicy = 0;

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        var item = Get(report, DiagnosticIds.ShadowPolicy);
        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Null(item.FixId);
        Assert.Equal("Shadow заблокирован политикой", report.Verdict);
    }

    [Fact]
    public async Task Policy_with_consent_is_ok_with_prompt_hint()
    {
        var h = new Harness();
        h.Cim.ShadowPolicy = 1;

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        var item = Get(report, DiagnosticIds.ShadowPolicy);
        Assert.Equal(DiagnosticStatus.Ok, item.Status);
        Assert.Contains("запрос", item.Hint);
    }

    [Fact]
    public async Task Firewall_rule_off_is_fail_without_fix()
    {
        var h = new Harness();
        h.Cim.FirewallEnabled = false;

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        var item = Get(report, DiagnosticIds.ShadowFirewall);
        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Null(item.FixId);
        Assert.Contains("брандмауэра", report.Verdict);
    }

    [Fact]
    public async Task Allow_remote_rpc_missing_is_only_a_warning()
    {
        var h = new Harness();
        h.Cim.AllowRemoteRpc = null;

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Warning, Get(report, DiagnosticIds.AllowRemoteRpc).Status);
        Assert.Contains("есть предупреждения", report.Verdict);
    }

    [Fact]
    public async Task Session_creation_failure_makes_all_cim_checks_unknown_with_reason()
    {
        var h = new Harness { FactoryError = new CimRemoteException(CimFailureKind.Connection, "не удалось подключиться по WinRM: таймаут") };

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        foreach (var id in DiagnosticIds.CimChecks)
        {
            Assert.Equal(DiagnosticStatus.Unknown, Get(report, id).Status);
            Assert.Contains("не удалось подключиться по WinRM: таймаут", Get(report, id).Detail);
        }
    }

    [Fact]
    public async Task Fatal_cim_error_stops_remaining_queries()
    {
        var h = new Harness();
        h.Cim.ServiceError = new CimRemoteException(CimFailureKind.AccessDenied, "нет прав для удалённого управления через WinRM (доступ запрещён)");

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        foreach (var id in DiagnosticIds.CimChecks)
            Assert.Equal(DiagnosticStatus.Unknown, Get(report, id).Status);
        Assert.Equal(["service TermService"], h.Cim.Calls);
        Assert.True(h.Cim.Disposed);
    }

    [Fact]
    public async Task Non_fatal_cim_error_affects_only_that_check()
    {
        var h = new Harness();
        h.Cim.FirewallError = new CimRemoteException(CimFailureKind.NotAvailable, "на компьютере нет нужного компонента управления");

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Unknown, Get(report, DiagnosticIds.ShadowFirewall).Status);
        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.TermService).Status);
        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.ShadowPolicy).Status);
        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.AllowRemoteRpc).Status);
    }

    [Fact]
    public async Task Registry_failure_does_not_break_other_checks()
    {
        var h = new Harness();
        h.Cim.RegistryError = new CimRemoteException(CimFailureKind.Other, "не удалось прочитать реестр компьютера (код 1)");

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Unknown, Get(report, DiagnosticIds.ShadowPolicy).Status);
        Assert.Equal(DiagnosticStatus.Unknown, Get(report, DiagnosticIds.AllowRemoteRpc).Status);
        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.ShadowFirewall).Status);
    }

    [Fact]
    public async Task Unexpected_exception_in_one_check_becomes_unknown_and_other_checks_still_run()
    {
        var h = new Harness();
        h.Cim.FirewallError = new InvalidCastException("Unable to cast object.\nвторая строка");

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        var item = Get(report, DiagnosticIds.ShadowFirewall);
        Assert.Equal(DiagnosticStatus.Unknown, item.Status);
        Assert.Contains("непредвиденная ошибка (Unable to cast object", item.Detail);
        Assert.DoesNotContain("вторая строка", item.Detail);
        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.TermService).Status);
        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.AllowRemoteRpc).Status);
        Assert.True(h.Cim.Disposed);
    }

    [Fact]
    public async Task Unexpected_exception_while_opening_session_makes_all_cim_checks_unknown()
    {
        var h = new Harness { FactoryError = new DllNotFoundException("mi.dll") };

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        foreach (var id in DiagnosticIds.CimChecks)
        {
            Assert.Equal(DiagnosticStatus.Unknown, Get(report, id).Status);
            Assert.Contains("mi.dll", Get(report, id).Detail);
        }

        Assert.Equal(DiagnosticStatus.Ok, Get(report, DiagnosticIds.Port445).Status);
    }

    [Fact]
    public async Task Hung_cim_returns_unknown_at_deadline_instead_of_hanging()
    {
        using var release = new ManualResetEventSlim();
        var h = new Harness { CimDeadline = TimeSpan.FromMilliseconds(300) };
        h.Cim.OnGetService = () => release.Wait();

        var started = DateTime.UtcNow;
        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;
        release.Set();

        Assert.True(elapsed < TimeSpan.FromSeconds(8));
        foreach (var id in DiagnosticIds.CimChecks)
        {
            var item = Get(report, id);
            Assert.Equal(DiagnosticStatus.Unknown, item.Status);
            Assert.Contains("не ответил по WinRM", item.Detail);
        }
    }

    [Fact]
    public async Task Explicit_credential_is_passed_to_cim_and_current_user_is_null()
    {
        var credential = new AccountCredential("CORP", "adm-ivanov", "p@ss");
        var explicitHarness = new Harness { Connection = new FakeConnection { Credential = credential } };
        await explicitHarness.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);
        Assert.Same(credential, explicitHarness.FactoryCredential);

        var currentUser = new Harness();
        await currentUser.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);
        Assert.Null(currentUser.FactoryCredential);
    }

    [Fact]
    public async Task Report_never_contains_the_password()
    {
        var credential = new AccountCredential("CORP", "adm-ivanov", "Sup3r-Secret-Pw");
        var h = new Harness { Connection = new FakeConnection { Credential = credential } };
        h.Cim.ShadowPolicy = 0;
        h.Cim.Service = new ServiceState("Stopped", "Manual");

        var report = await h.Build().RunAsync(TestComputers.Pc(), CancellationToken.None);

        var text = string.Join("|", report.Items.Select(i => $"{i.Title} {i.Detail} {i.Hint} {i.FixId}")) + report.Verdict;
        Assert.DoesNotContain("Sup3r-Secret-Pw", text);
    }

    [Fact]
    public async Task Invalid_host_and_missing_login_are_rejected()
    {
        var h = new Harness();
        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => h.Build().RunAsync(TestComputers.Pc("-evil", dns: null), CancellationToken.None));
        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);

        var service = new CimDiagnosticsService(TestConnections.LoggedOut(), new FakeProbe(), (_, _) => new FakeCim(), (_, _) => Task.FromResult<IPAddress[]>([]), TimeSpan.FromSeconds(1));
        ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.RunAsync(TestComputers.Pc(), CancellationToken.None));
        Assert.Equal(OperationFailure.NotLoggedIn, ex.Failure);
    }

    // FixAsync

    [Fact]
    public async Task Fix_start_termservice_calls_start_service()
    {
        var h = new Harness();

        await h.Build().FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None);

        Assert.Equal(["start TermService"], h.Cim.Calls);
        Assert.True(h.Cim.Disposed);
        Assert.Equal("pc-acc-014.corp.local", h.FactoryHost);
    }

    [Fact]
    public async Task Fix_already_running_code_10_is_success()
    {
        var h = new Harness();
        h.Cim.StartServiceResult = 10;

        await h.Build().FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None);
    }

    [Theory]
    [InlineData("shadow-policy")]
    [InlineData("shadow-firewall")]
    [InlineData("")]
    [InlineData("start-termservice ")]
    public async Task Fix_other_ids_are_rejected_and_touch_nothing(string fixId)
    {
        var h = new Harness();

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() => h.Build().FixAsync(TestComputers.Pc(), fixId, CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        Assert.Equal(0, h.FactoryCalls);
    }

    [Theory]
    [InlineData(2u, OperationFailure.AccessDenied, "доступ запрещён")]
    [InlineData(14u, OperationFailure.Other, "служба отключена")]
    [InlineData(7u, OperationFailure.Other, "время ожидания")]
    public async Task Fix_failure_codes_become_operation_failed(uint code, OperationFailure failure, string text)
    {
        var h = new Harness();
        h.Cim.StartServiceResult = code;

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => h.Build().FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None));

        Assert.Equal(failure, ex.Failure);
        Assert.Contains(text, ex.Message);
        Assert.EndsWith($"(код {code}).", ex.Message);
        Assert.Contains("Не удалось запустить службу TermService на компьютере pc-acc-014.corp.local", ex.Message);
    }

    [Fact]
    public async Task Fix_cim_errors_are_translated()
    {
        var h = new Harness();
        h.Cim.ServiceError = new CimRemoteException(CimFailureKind.AccessDenied, "нет прав для удалённого управления через WinRM (доступ запрещён)");

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => h.Build().FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("нет прав для удалённого управления", ex.Message);
    }

    [Fact]
    public async Task Fix_unexpected_exception_is_wrapped()
    {
        var h = new Harness { FactoryError = new InvalidOperationException("внутренний сбой") };

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => h.Build().FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None));

        Assert.Equal(OperationFailure.Other, ex.Failure);
        Assert.Contains("внутренний сбой", ex.Message);
        Assert.Contains("pc-acc-014.corp.local", ex.Message);
    }

    [Fact]
    public async Task Fix_session_failure_is_translated()
    {
        var h = new Harness { FactoryError = new CimRemoteException(CimFailureKind.Connection, "не удалось подключиться по WinRM: таймаут") };

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => h.Build().FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None));

        Assert.Equal(OperationFailure.Unreachable, ex.Failure);
    }

    [Fact]
    public async Task Fix_hung_cim_times_out()
    {
        using var release = new ManualResetEventSlim();
        var h = new Harness { CimDeadline = TimeSpan.FromMilliseconds(300) };
        h.Cim.ServiceError = null;
        var service = new CimDiagnosticsService(
            TestConnections.LoggedIn(h.Connection),
            new FakeProbe(),
            (_, _) =>
            {
                release.Wait();
                return h.Cim;
            },
            (_, _) => Task.FromResult<IPAddress[]>([]),
            TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() => service.FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None));
        release.Set();

        Assert.Equal(OperationFailure.Timeout, ex.Failure);
    }
}
