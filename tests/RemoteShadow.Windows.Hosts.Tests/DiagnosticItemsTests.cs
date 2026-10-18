using Microsoft.Management.Infrastructure;
using System.Net;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts.Tests;

public class DiagnosticItemsTests
{
    [Fact]
    public void Port_445_closed_is_fail()
    {
        var item = DiagnosticItems.Port(445, open: false);

        Assert.Equal("port-445", item.Id);
        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Null(item.FixId);
    }

    [Theory]
    [InlineData(135)]
    [InlineData(3389)]
    [InlineData(5985)]
    public void Other_closed_ports_are_warnings(int port)
    {
        var item = DiagnosticItems.Port(port, open: false);

        Assert.Equal($"port-{port}", item.Id);
        Assert.Equal(DiagnosticStatus.Warning, item.Status);
    }

    [Theory]
    [InlineData(135)]
    [InlineData(445)]
    [InlineData(3389)]
    [InlineData(5985)]
    public void Open_ports_are_ok(int port) =>
        Assert.Equal(DiagnosticStatus.Ok, DiagnosticItems.Port(port, open: true).Status);

    [Fact]
    public void Dns_failure_hints_at_dns_and_vpn()
    {
        var item = DiagnosticItems.DnsFailed("pc1");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("DNS", item.Hint);
        Assert.Contains("VPN", item.Hint);
    }

    [Fact]
    public void Dns_resolved_lists_addresses()
    {
        var item = DiagnosticItems.DnsResolved("pc1.corp.local", [IPAddress.Parse("10.0.0.5")]);

        Assert.Equal(DiagnosticStatus.Ok, item.Status);
        Assert.Contains("10.0.0.5", item.Detail);
    }

    [Fact]
    public void Running_termservice_is_ok()
    {
        var item = DiagnosticItems.TermService(new ServiceState("Running", "Auto"));

        Assert.Equal(DiagnosticStatus.Ok, item.Status);
        Assert.Null(item.FixId);
    }

    [Theory]
    [InlineData("Stopped", "Auto")]
    [InlineData("Stopped", "Manual")]
    [InlineData("Start Pending", "Auto")]
    public void Stopped_termservice_is_fail_with_start_fix(string state, string startMode)
    {
        var item = DiagnosticItems.TermService(new ServiceState(state, startMode));

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Equal("start-termservice", item.FixId);
    }

    [Fact]
    public void Disabled_termservice_cannot_be_started_by_the_fix()
    {
        var item = DiagnosticItems.TermService(new ServiceState("Stopped", "Disabled"));

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Null(item.FixId);
        Assert.Contains("отключена", item.Hint);
    }

    [Fact]
    public void Missing_termservice_is_fail_without_fix()
    {
        var item = DiagnosticItems.TermService(null);

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Null(item.FixId);
    }

    [Fact]
    public void Firewall_enabled_is_ok()
    {
        var item = DiagnosticItems.ShadowFirewall(true);

        Assert.Equal(DiagnosticStatus.Ok, item.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Firewall_disabled_or_missing_is_fail_without_fix_and_mentions_gpo(bool? enabled)
    {
        var item = DiagnosticItems.ShadowFirewall(enabled);

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Null(item.FixId);
        Assert.Contains("Remote Desktop - Shadow (TCP-In)", item.Detail);
        Assert.Contains("групповой политикой", item.Hint);
    }

    [Fact]
    public void AllowRemoteRpc_one_is_ok_and_others_are_informational_warning()
    {
        Assert.Equal(DiagnosticStatus.Ok, DiagnosticItems.AllowRemoteRpc(1).Status);

        var zero = DiagnosticItems.AllowRemoteRpc(0);
        var missing = DiagnosticItems.AllowRemoteRpc(null);
        Assert.Equal(DiagnosticStatus.Warning, zero.Status);
        Assert.Equal(DiagnosticStatus.Warning, missing.Status);
        Assert.Null(zero.FixId);
        Assert.Contains("не задан", missing.Detail);
    }

    [Fact]
    public void WinRm_unavailable_marks_every_cim_check_unknown_with_the_spec_text()
    {
        var items = DiagnosticItems.WinRmUnavailable().ToList();

        Assert.Equal(DiagnosticIds.CimChecks.Count, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal(DiagnosticStatus.Unknown, i.Status);
            Assert.Contains("не удалось проверить: WinRM (5985) недоступен", i.Detail, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(DiagnosticIds.CimChecks, items.Select(i => i.Id).ToArray());
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(10u, true)]
    [InlineData(2u, false)]
    [InlineData(14u, false)]
    [InlineData(99u, false)]
    public void StartService_return_code_success(uint code, bool success) =>
        Assert.Equal(success, ServiceReturnCodes.IsSuccess(code));

    [Fact]
    public void StartService_codes_have_russian_descriptions()
    {
        Assert.Contains("доступ запрещён", ServiceReturnCodes.Describe(2));
        Assert.Contains("отключена", ServiceReturnCodes.Describe(14));
        Assert.Equal("неизвестный код", ServiceReturnCodes.Describe(500));
    }
}

public class CimHelpersTests
{
    [Fact]
    public void ClassifyRegistryReturn()
    {
        Assert.Equal(RegistryReadOutcome.Value, CimHelpers.ClassifyRegistryReturn(0));
        Assert.Equal(RegistryReadOutcome.NotFound, CimHelpers.ClassifyRegistryReturn(2));
        Assert.Equal(RegistryReadOutcome.NotFound, CimHelpers.ClassifyRegistryReturn(3));
        Assert.Equal(RegistryReadOutcome.AccessDenied, CimHelpers.ClassifyRegistryReturn(5));
        Assert.Equal(RegistryReadOutcome.Error, CimHelpers.ClassifyRegistryReturn(1));
        Assert.Equal(RegistryReadOutcome.Error, CimHelpers.ClassifyRegistryReturn(1722));
    }

    [Theory]
    [InlineData("TermService", "TermService")]
    [InlineData("O'Brien", "O\\'Brien")]
    [InlineData(@"a\b", @"a\\b")]
    public void WqlEscape(string input, string expected) =>
        Assert.Equal(expected, CimHelpers.WqlEscape(input));

    [Fact]
    public void ShortMessage_takes_first_line_and_limits_length()
    {
        Assert.Equal("первая", CimHelpers.ShortMessage("первая\nвторая\nтретья"));
        Assert.Equal(200, CimHelpers.ShortMessage(new string('x', 1000)).Length);
        Assert.Equal("причина не указана", CimHelpers.ShortMessage("  "));
        Assert.Equal("причина не указана", CimHelpers.ShortMessage(null));
    }

    [Theory]
    [InlineData(NativeErrorCode.InvalidNamespace, true)]
    [InlineData(NativeErrorCode.InvalidClass, true)]
    [InlineData(NativeErrorCode.NotFound, true)]
    [InlineData(NativeErrorCode.NotSupported, true)]
    [InlineData(NativeErrorCode.MethodNotFound, true)]
    [InlineData(NativeErrorCode.MethodNotAvailable, true)]
    [InlineData(NativeErrorCode.InvalidQuery, true)]
    [InlineData(NativeErrorCode.AccessDenied, false)]
    [InlineData(NativeErrorCode.Failed, false)]
    public void IsMissingComponent_matches_only_absent_namespace_class_or_method(NativeErrorCode code, bool expected) =>
        Assert.Equal(expected, CimHelpers.IsMissingComponent(code));

    [Fact]
    public void CimRemoteException_fatal_kinds()
    {
        Assert.True(new CimRemoteException(CimFailureKind.Connection, "x").IsFatal);
        Assert.True(new CimRemoteException(CimFailureKind.AccessDenied, "x").IsFatal);
        Assert.False(new CimRemoteException(CimFailureKind.NotAvailable, "x").IsFatal);
        Assert.False(new CimRemoteException(CimFailureKind.Other, "x").IsFatal);
    }
}
