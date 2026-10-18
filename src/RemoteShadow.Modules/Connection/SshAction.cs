using RemoteShadow.Core;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>
/// SSH-подключение: Windows Terminal (<c>wt.exe ssh user@host</c>), а без него — обычный <c>ssh.exe user@host</c>.
/// Логин спрашивается один раз в <see cref="PrepareAsync"/> и передаётся параметром <see cref="UserParameter"/>.
/// </summary>
public sealed class SshAction : IAction<ComputerTarget>
{
    /// <summary>Имя параметра с логином, который выбрал админ.</summary>
    public const string UserParameter = "sshUser";

    private const string DialogTitle = "SSH";

    public string Id => "connection.ssh";
    public string Title => "SSH";
    public string Group => ConnectionModule.GroupName;
    public string? Icon => "terminal-2";
    public int Order => 40;
    public string? Description => "Открыть SSH-сессию на компьютере";
    public ActionDanger Danger => ActionDanger.None;
    public bool SupportsBulk => false;

    public bool CanExecute(ComputerTarget target, ActionContext context) => true;

    public async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IReadOnlyList<ComputerTarget> targets, ActionContext context, CancellationToken ct)
    {
        var computer = targets.Count > 0 ? targets[0].Computer.Name : string.Empty;
        var message = $"Логин для подключения к {computer} по SSH:";
        var initial = SshCommand.DefaultLogin(context.Connection.AccountName);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var input = await context.Dialogs.PromptTextAsync(DialogTitle, message, initial);
            if (input is null) return null;

            var login = input.Trim();
            if (SshCommand.IsValidLogin(login))
                return new Dictionary<string, string> { [UserParameter] = login };

            await context.Dialogs.ShowErrorAsync(DialogTitle,
                "Недопустимый логин. Допустимы латинские буквы, цифры, точка, дефис и подчёркивание, " +
                "до 64 символов, без дефиса в начале. Формат user@domain не поддерживается.");
            initial = login;
        }
    }

    public async Task<ActionResult> ExecuteAsync(ComputerTarget target, ActionContext context, CancellationToken ct)
    {
        var login = context.GetParameter(UserParameter)?.Trim();
        if (!SshCommand.IsValidLogin(login))
            throw new OperationFailedException("Не указан или недопустим логин для SSH.", OperationFailure.InvalidInput);

        var destination = SshCommand.Destination(login!, target.Computer.HostName);

        // ssh.exe нужен в любом случае: Windows Terminal лишь запускает его во вкладке.
        if (context.Launcher.FindExecutable(MstscCommand.SshExe) is not { } ssh)
            throw new OperationFailedException(
                "Не найден ssh.exe. Включите компонент «Клиент OpenSSH» в Windows (дополнительные компоненты системы).",
                OperationFailure.NotFound);

        var terminal = context.Launcher.FindExecutable(MstscCommand.WindowsTerminalExe);
        if (terminal is not null)
            await context.Launcher.StartAsync(terminal, ["ssh", destination], ct);
        else
            await context.Launcher.StartAsync(ssh, [destination], ct);

        context.AuditDetails = $"ssh: {destination}, {(terminal is not null ? "Windows Terminal" : "консоль")}";
        return ActionResult.Ok("SSH-сессия запускается");
    }
}
