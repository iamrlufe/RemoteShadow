using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Обычное RDP-подключение к компьютеру через mstsc.</summary>
public sealed class RdpAction : IAction<ComputerTarget>
{
    public string Id => "connection.rdp";
    public string Title => "RDP";
    public string Group => ConnectionModule.GroupName;
    public string? Icon => "device-desktop";
    public int Order => 30;
    public string? Description => "Подключиться к рабочему столу компьютера (mstsc)";
    public ActionDanger Danger => ActionDanger.None;
    public bool SupportsBulk => false;

    public bool CanExecute(ComputerTarget target, ActionContext context) => true;

    public async Task<ActionResult> ExecuteAsync(ComputerTarget target, ActionContext context, CancellationToken ct)
    {
        var args = MstscCommand.Rdp(target.Computer.HostName);
        await context.Launcher.StartAsync(HostGuard.ResolveExecutable(context.Launcher, MstscCommand.MstscExe), args, ct);
        return ActionResult.Ok("Подключение запущено");
    }
}
