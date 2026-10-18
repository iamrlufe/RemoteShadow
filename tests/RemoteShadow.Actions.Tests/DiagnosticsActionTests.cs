using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class DiagnosticsActionTests
{
    private static DiagnosticItem Item(string? fixId = "start-termservice") =>
        new("termservice", "Служба TermService", DiagnosticStatus.Fail, "Остановлена", "Запустите службу", fixId);

    [Fact]
    public void Target_label_and_kind()
    {
        var target = new DiagnosticFixTarget(Samples.Computer("PC1"), Item());

        Assert.Equal("computer", target.Kind);
        Assert.Equal("PC1: Служба TermService", target.Label);
        Assert.IsAssignableFrom<IActionTarget>(target);
    }

    [Fact]
    public void Metadata()
    {
        var a = new ApplyDiagnosticFixAction();
        Assert.Equal("Исправить", a.Title);
        Assert.Equal("Диагностика", a.Group);
        Assert.Equal("tool", a.Icon);
        Assert.Equal(ActionDanger.Confirm, a.Danger);
    }

    [Fact]
    public void CanExecute_only_when_item_has_fix()
    {
        var env = new TestEnv();
        var a = new ApplyDiagnosticFixAction();

        var fixable = new DiagnosticFixTarget(Samples.Computer(), Item("start-termservice"));
        var notFixable = new DiagnosticFixTarget(Samples.Computer(), Item(null));

        Assert.True(a.CanExecute(fixable, env.Context()));
        Assert.Null(a.GetUnavailableReason(fixable, env.Context()));
        Assert.False(a.CanExecute(notFixable, env.Context()));
        Assert.False(string.IsNullOrWhiteSpace(a.GetUnavailableReason(notFixable, env.Context())));
    }

    [Fact]
    public async Task Execute_calls_diagnostics_fix_and_audits_fix_id()
    {
        var env = new TestEnv();
        var computer = Samples.Computer();
        var ctx = env.Context();

        var result = await new ApplyDiagnosticFixAction().ExecuteAsync(
            new DiagnosticFixTarget(computer, Item("start-termservice")), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        var (fixedComputer, fixId) = Assert.Single(env.Diagnostics.Fixes);
        Assert.Same(computer, fixedComputer);
        Assert.Equal("start-termservice", fixId);
        Assert.Equal("start-termservice", ctx.AuditDetails);
    }

    [Fact]
    public async Task Execute_without_fix_id_fails_and_calls_nothing()
    {
        var env = new TestEnv();

        await Assert.ThrowsAsync<OperationFailedException>(() => new ApplyDiagnosticFixAction().ExecuteAsync(
            new DiagnosticFixTarget(Samples.Computer(), Item(null)), env.Context(), CancellationToken.None));

        Assert.Empty(env.Diagnostics.Fixes);
    }

    [Fact]
    public async Task Execute_resolves_diagnostics_service_from_context_services()
    {
        // Сервис берётся из контейнера в момент выполнения, а не из конструктора действия.
        var env = new TestEnv();
        var empty = new ActionContext(new SimpleServiceProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ApplyDiagnosticFixAction().ExecuteAsync(
            new DiagnosticFixTarget(Samples.Computer(), Item()), empty, CancellationToken.None));
        Assert.Empty(env.Diagnostics.Fixes);
    }
}
