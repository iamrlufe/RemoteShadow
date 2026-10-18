using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Открывает административную общую папку C$ компьютера в проводнике.</summary>
public sealed class OpenAdminShareAction : IAction<ComputerTarget>
{
    public string Id => "connection.open-admin-share";
    public string Title => "Открыть C$";
    public string Group => ConnectionModule.GroupName;
    public string? Icon => "folder";
    public int Order => 50;
    public string? Description => "Открыть административную папку C$ в проводнике";
    public ActionDanger Danger => ActionDanger.None;
    public bool SupportsBulk => false;

    public bool CanExecute(ComputerTarget target, ActionContext context) => true;

    public async Task<ActionResult> ExecuteAsync(ComputerTarget target, ActionContext context, CancellationToken ct)
    {
        var host = HostGuard.Require(target.Computer.HostName);
        await context.Launcher.StartAsync(
            HostGuard.ResolveExecutable(context.Launcher, MstscCommand.ExplorerExe), [$@"\\{host}\C$"], ct);
        return ActionResult.Ok("Папка открывается в проводнике");
    }
}
