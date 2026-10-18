using RemoteShadow.Core;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Сообщение пользователю сессии (WTSSendMessage). Текст спрашивается один раз на всю пачку.</summary>
public sealed class SendMessageAction : IAction<SessionTarget>
{
    /// <summary>Имя параметра с текстом сообщения.</summary>
    public const string MessageParameter = "message";

    /// <summary>Заголовок окна, которое увидит пользователь.</summary>
    public const string WindowTitle = "Сообщение от администратора";

    private const int AuditPreviewLength = 80;

    public string Id => "sessions.send-message";
    public string Title => "Сообщение";
    public string Group => SessionsModule.GroupName;
    public string? Icon => "message";
    public int Order => 10;
    public string? Description => "Показать пользователю окно с сообщением";
    public ActionDanger Danger => ActionDanger.None;
    public bool SupportsBulk => true;

    public bool CanExecute(SessionTarget target, ActionContext context) => target.Session.HasUser;

    public string? GetUnavailableReason(SessionTarget target, ActionContext context) =>
        target.Session.HasUser ? null : "В сессии нет пользователя";

    public async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IReadOnlyList<SessionTarget> targets, ActionContext context, CancellationToken ct)
    {
        var prompt = targets.Count > 1
            ? $"Текст сообщения для выбранных сессий ({targets.Count}):"
            : "Текст сообщения пользователю:";

        var text = await context.Dialogs.PromptTextAsync("Сообщение пользователю", prompt, multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return null;

        return new Dictionary<string, string> { [MessageParameter] = text.Trim() };
    }

    public async Task<ActionResult> ExecuteAsync(SessionTarget target, ActionContext context, CancellationToken ct)
    {
        var host = HostGuard.Require(target.Computer.HostName);
        var text = context.GetParameter(MessageParameter);
        if (string.IsNullOrWhiteSpace(text))
            throw new OperationFailedException("Не задан текст сообщения.", OperationFailure.InvalidInput);

        await context.Sessions.SendMessageAsync(host, target.Session.Id, WindowTitle, text, ct);

        context.AuditDetails = $"сообщение: {text.Length} симв., начало: «{Preview(text)}»";
        return ActionResult.Ok("Сообщение отправлено");
    }

    /// <summary>Первые 80 символов в одну строку; многоточие, если текст длиннее.</summary>
    public static string Preview(string text)
    {
        var oneLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (oneLine.Length <= AuditPreviewLength) return oneLine;

        // Не режем суррогатную пару пополам.
        var length = char.IsHighSurrogate(oneLine[AuditPreviewLength - 1]) ? AuditPreviewLength - 1 : AuditPreviewLength;
        return oneLine[..length] + "…";
    }
}

/// <summary>Отключает сессию: программы пользователя продолжают работать, он может переподключиться.</summary>
public sealed class DisconnectSessionAction : IAction<SessionTarget>
{
    public string Id => "sessions.disconnect";
    public string Title => "Отключить";
    public string Group => SessionsModule.GroupName;
    public string? Icon => "plug-x";
    public int Order => 20;
    public string? Description => "Отключить сессию: программы пользователя продолжат работать";
    public ActionDanger Danger => ActionDanger.Confirm;
    public bool SupportsBulk => true;

    public bool CanExecute(SessionTarget target, ActionContext context) => target.Session.HasUser;

    public string? GetUnavailableReason(SessionTarget target, ActionContext context) =>
        target.Session.HasUser ? null : "В сессии нет пользователя";

    public async Task<ActionResult> ExecuteAsync(SessionTarget target, ActionContext context, CancellationToken ct)
    {
        var host = HostGuard.Require(target.Computer.HostName);
        await context.Sessions.DisconnectAsync(host, target.Session.Id, ct);
        return ActionResult.Ok("Сессия отключена");
    }
}

/// <summary>Завершает сессию (выход из системы). Несохранённые данные пользователя теряются.</summary>
public sealed class LogoffSessionAction : IAction<SessionTarget>
{
    public string Id => "sessions.logoff";
    public string Title => "Выйти из системы";
    public string Group => SessionsModule.GroupName;
    public string? Icon => "logout";
    public int Order => 30;
    public string? Description => "Завершить сессию: несохранённые данные пользователя будут потеряны";
    public ActionDanger Danger => ActionDanger.ConfirmWithReason;
    public bool SupportsBulk => true;

    public bool CanExecute(SessionTarget target, ActionContext context) => target.Session.HasUser;

    public string? GetUnavailableReason(SessionTarget target, ActionContext context) =>
        target.Session.HasUser ? null : "В сессии нет пользователя";

    public async Task<ActionResult> ExecuteAsync(SessionTarget target, ActionContext context, CancellationToken ct)
    {
        var host = HostGuard.Require(target.Computer.HostName);
        await context.Sessions.LogoffAsync(host, target.Session.Id, ct);
        return ActionResult.Ok("Сессия завершена");
    }
}
