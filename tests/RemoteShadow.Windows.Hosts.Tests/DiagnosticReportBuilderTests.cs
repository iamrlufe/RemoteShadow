using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts.Tests;

public class DiagnosticReportBuilderTests
{
    private static DiagnosticItem Item(string id, DiagnosticStatus status) =>
        new(id, DiagnosticItems.TitleOf(id), status, "detail");

    private static List<DiagnosticItem> AllOk() =>
    [
        Item(DiagnosticIds.Dns, DiagnosticStatus.Ok),
        Item(DiagnosticIds.Port135, DiagnosticStatus.Ok),
        Item(DiagnosticIds.Port445, DiagnosticStatus.Ok),
        Item(DiagnosticIds.Port3389, DiagnosticStatus.Ok),
        Item(DiagnosticIds.Port5985, DiagnosticStatus.Ok),
        Item(DiagnosticIds.TermService, DiagnosticStatus.Ok),
        Item(DiagnosticIds.ShadowPolicy, DiagnosticStatus.Ok),
        Item(DiagnosticIds.ShadowFirewall, DiagnosticStatus.Ok),
        Item(DiagnosticIds.AllowRemoteRpc, DiagnosticStatus.Ok),
    ];

    private static List<DiagnosticItem> With(params (string Id, DiagnosticStatus Status)[] overrides)
    {
        var items = AllOk();
        foreach (var (id, status) in overrides)
        {
            var i = items.FindIndex(x => x.Id == id);
            items[i] = Item(id, status);
        }

        return items;
    }

    [Fact]
    public void All_ok_verdict_is_all_clear()
    {
        var verdict = DiagnosticReportBuilder.Verdict(AllOk());

        Assert.StartsWith("Всё в порядке", verdict);
    }

    [Fact]
    public void Port_445_closed_example_from_spec()
    {
        var verdict = DiagnosticReportBuilder.Verdict(With((DiagnosticIds.Port445, DiagnosticStatus.Fail)));

        Assert.Equal("Shadow заблокирован: порт 445 закрыт", verdict);
    }

    [Fact]
    public void Policy_fail_verdict_names_policy() =>
        Assert.Equal(
            "Shadow заблокирован политикой",
            DiagnosticReportBuilder.Verdict(With((DiagnosticIds.ShadowPolicy, DiagnosticStatus.Fail))));

    [Fact]
    public void TermService_fail_verdict_names_service()
    {
        var verdict = DiagnosticReportBuilder.Verdict(With((DiagnosticIds.TermService, DiagnosticStatus.Fail)));

        Assert.Contains("TermService", verdict);
    }

    [Fact]
    public void Firewall_fail_verdict_names_rule()
    {
        var verdict = DiagnosticReportBuilder.Verdict(With((DiagnosticIds.ShadowFirewall, DiagnosticStatus.Fail)));

        Assert.Contains("Remote Desktop - Shadow (TCP-In)", verdict);
    }

    [Fact]
    public void Priority_dns_beats_everything()
    {
        var items = With(
            (DiagnosticIds.Dns, DiagnosticStatus.Fail),
            (DiagnosticIds.Port445, DiagnosticStatus.Fail),
            (DiagnosticIds.TermService, DiagnosticStatus.Fail),
            (DiagnosticIds.ShadowPolicy, DiagnosticStatus.Fail),
            (DiagnosticIds.ShadowFirewall, DiagnosticStatus.Fail));

        Assert.Contains("DNS", DiagnosticReportBuilder.Verdict(items));
    }

    [Fact]
    public void Priority_port_445_beats_service_policy_and_firewall()
    {
        var items = With(
            (DiagnosticIds.Port445, DiagnosticStatus.Fail),
            (DiagnosticIds.TermService, DiagnosticStatus.Fail),
            (DiagnosticIds.ShadowPolicy, DiagnosticStatus.Fail),
            (DiagnosticIds.ShadowFirewall, DiagnosticStatus.Fail));

        Assert.Equal("Shadow заблокирован: порт 445 закрыт", DiagnosticReportBuilder.Verdict(items));
    }

    [Fact]
    public void Priority_service_beats_policy_and_firewall()
    {
        var items = With(
            (DiagnosticIds.TermService, DiagnosticStatus.Fail),
            (DiagnosticIds.ShadowPolicy, DiagnosticStatus.Fail),
            (DiagnosticIds.ShadowFirewall, DiagnosticStatus.Fail));

        Assert.Contains("TermService", DiagnosticReportBuilder.Verdict(items));
    }

    [Fact]
    public void Priority_policy_beats_firewall()
    {
        var items = With(
            (DiagnosticIds.ShadowPolicy, DiagnosticStatus.Fail),
            (DiagnosticIds.ShadowFirewall, DiagnosticStatus.Fail));

        Assert.Equal("Shadow заблокирован политикой", DiagnosticReportBuilder.Verdict(items));
    }

    [Fact]
    public void Fail_beats_unknown_and_warning()
    {
        var items = With(
            (DiagnosticIds.Port3389, DiagnosticStatus.Warning),
            (DiagnosticIds.ShadowPolicy, DiagnosticStatus.Unknown),
            (DiagnosticIds.ShadowFirewall, DiagnosticStatus.Fail));

        Assert.Contains("брандмауэра", DiagnosticReportBuilder.Verdict(items));
    }

    [Fact]
    public void Unknown_without_failures_says_checks_were_incomplete()
    {
        var items = With(
            (DiagnosticIds.Port5985, DiagnosticStatus.Warning),
            (DiagnosticIds.TermService, DiagnosticStatus.Unknown),
            (DiagnosticIds.ShadowPolicy, DiagnosticStatus.Unknown));

        var verdict = DiagnosticReportBuilder.Verdict(items);

        Assert.Contains("часть проверок выполнить не удалось", verdict);
    }

    [Fact]
    public void Warnings_only_say_so()
    {
        var items = With((DiagnosticIds.Port3389, DiagnosticStatus.Warning));

        Assert.Contains("есть предупреждения", DiagnosticReportBuilder.Verdict(items));
    }

    [Fact]
    public void Unrecognised_failure_is_named_by_title()
    {
        var items = AllOk();
        items.Add(new DiagnosticItem("custom", "Особая проверка", DiagnosticStatus.Fail, "x"));

        Assert.Equal("Найдена проблема: Особая проверка", DiagnosticReportBuilder.Verdict(items));
    }

    [Fact]
    public void Empty_list_is_all_clear() =>
        Assert.StartsWith("Всё в порядке", DiagnosticReportBuilder.Verdict([]));

    [Fact]
    public void Order_follows_display_order_and_puts_unknown_ids_last_in_input_order()
    {
        var shuffled = new List<DiagnosticItem>
        {
            Item(DiagnosticIds.AllowRemoteRpc, DiagnosticStatus.Ok),
            new("zzz", "Z", DiagnosticStatus.Ok, "z"),
            Item(DiagnosticIds.TermService, DiagnosticStatus.Ok),
            Item(DiagnosticIds.Dns, DiagnosticStatus.Ok),
            new("aaa", "A", DiagnosticStatus.Ok, "a"),
            Item(DiagnosticIds.Port445, DiagnosticStatus.Ok),
            Item(DiagnosticIds.Port5985, DiagnosticStatus.Ok),
            Item(DiagnosticIds.ShadowPolicy, DiagnosticStatus.Ok),
            Item(DiagnosticIds.Port135, DiagnosticStatus.Ok),
            Item(DiagnosticIds.ShadowFirewall, DiagnosticStatus.Ok),
            Item(DiagnosticIds.Port3389, DiagnosticStatus.Ok),
        };

        var ids = DiagnosticReportBuilder.Order(shuffled).Select(i => i.Id).ToArray();

        Assert.Equal(
            ["dns", "port-135", "port-445", "port-3389", "port-5985", "termservice", "shadow-policy", "shadow-firewall", "allow-remote-rpc", "zzz", "aaa"],
            ids);
    }

    [Fact]
    public void Build_sets_host_time_order_and_verdict()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var items = new[]
        {
            Item(DiagnosticIds.ShadowPolicy, DiagnosticStatus.Fail),
            Item(DiagnosticIds.Dns, DiagnosticStatus.Ok),
        };

        var report = DiagnosticReportBuilder.Build("pc1", items, created);

        Assert.Equal("pc1", report.Host);
        Assert.Equal(created, report.CreatedUtc);
        Assert.Equal(DiagnosticIds.Dns, report.Items[0].Id);
        Assert.Equal("Shadow заблокирован политикой", report.Verdict);
        Assert.True(report.HasProblems);
    }

    [Fact]
    public void Every_known_check_has_a_title_and_unique_id()
    {
        Assert.All(DiagnosticIds.DisplayOrder, id => Assert.NotEqual(id, DiagnosticItems.TitleOf(id)));
        Assert.Equal(DiagnosticIds.DisplayOrder.Count, DiagnosticIds.DisplayOrder.Distinct().Count());
        Assert.All(DiagnosticIds.CimChecks, id => Assert.Contains(id, DiagnosticIds.DisplayOrder));
    }
}
