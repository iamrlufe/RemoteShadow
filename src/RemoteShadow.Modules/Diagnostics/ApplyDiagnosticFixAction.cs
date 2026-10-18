using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Modules;

/// <summary>Исправляет проблему, найденную диагностикой, если у проверки есть <c>FixId</c> (то, что не управляется GPO).</summary>
public sealed class ApplyDiagnosticFixAction : IAction<DiagnosticFixTarget>
{
    public string Id => "diagnostics.apply-fix";
    public string Title => "Исправить";
    public string Group => DiagnosticsModule.GroupName;
    public string? Icon => "tool";
    public int Order => 10;
    public string? Description => "Исправить найденную проблему на компьютере";
    public ActionDanger Danger => ActionDanger.Confirm;
    public bool SupportsBulk => false;

    public bool CanExecute(DiagnosticFixTarget target, ActionContext context) => target.Item.FixId is not null;

    public string? GetUnavailableReason(DiagnosticFixTarget target, ActionContext context) =>
        target.Item.FixId is null ? "Эту проблему нельзя исправить автоматически" : null;

    public async Task<ActionResult> ExecuteAsync(DiagnosticFixTarget target, ActionContext context, CancellationToken ct)
    {
        var fixId = target.Item.FixId
                    ?? throw new OperationFailedException("Эту проблему нельзя исправить автоматически.", OperationFailure.InvalidInput);
        HostGuard.Require(target.Computer.HostName);

        var diagnostics = context.Services.GetRequiredService<IDiagnosticsService>();
        await diagnostics.FixAsync(target.Computer, fixId, ct);

        context.AuditDetails = fixId;
        return ActionResult.Ok($"Исправлено: {target.Item.Title}. Запустите диагностику ещё раз, чтобы убедиться.");
    }
}
