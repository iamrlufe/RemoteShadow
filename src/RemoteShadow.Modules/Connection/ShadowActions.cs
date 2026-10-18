using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>
/// Shadow-подключение к сессии через mstsc. Общая часть для просмотра и управления.
/// Без запроса пользователю (<see cref="ActionParameters.NoConsent"/>) требует причину в журнале.
/// </summary>
public abstract class ShadowAction : IAction<SessionTarget>
{
    private readonly bool _control;

    protected ShadowAction(string id, string title, string icon, int order, string description, bool control)
    {
        Id = id;
        Title = title;
        Icon = icon;
        Order = order;
        Description = description;
        _control = control;
    }

    public string Id { get; }
    public string Title { get; }
    public string Group => ConnectionModule.GroupName;
    public string? Icon { get; }
    public int Order { get; }
    public string? Description { get; }
    public ActionDanger Danger => ActionDanger.None;
    public bool SupportsBulk => false;

    public ActionDanger GetDanger(SessionTarget target, ActionContext context) =>
        context.GetFlag(ActionParameters.NoConsent) ? ActionDanger.ConfirmWithReason : Danger;

    public bool CanExecute(SessionTarget target, ActionContext context) => target.Session.CanShadow;

    public string? GetUnavailableReason(SessionTarget target, ActionContext context) => target.Session.ShadowBlockedReason;

    public async Task<ActionResult> ExecuteAsync(SessionTarget target, ActionContext context, CancellationToken ct)
    {
        var noConsent = context.GetFlag(ActionParameters.NoConsent);
        var args = MstscCommand.Shadow(target.Computer.HostName, target.Session.Id, _control, noConsent);

        await context.Launcher.StartAsync(HostGuard.ResolveExecutable(context.Launcher, MstscCommand.MstscExe), args, ct);

        context.AuditDetails =
            $"shadow: {(_control ? "управление" : "просмотр")}, без запроса пользователю: {(noConsent ? "да" : "нет")}";

        var message = "Подключение запущено. Если политика требует согласия, пользователь увидит запрос.";
        if (noConsent)
        {
            // Для управления без запроса нужно значение политики Shadow = 2, для просмотра — 2 или 4.
            var values = _control ? "2" : "2 или 4";
            message += $" Подключение без запроса сработает, только если на компьютере политика Shadow имеет значение {values}; " +
                       "иначе mstsc вернёт ошибку. Что именно мешает, покажет диагностика.";
        }

        return ActionResult.Ok(message);
    }
}

/// <summary>Shadow: просмотр экрана сессии без управления.</summary>
public sealed class ShadowViewAction()
    : ShadowAction("connection.shadow-view", "Shadow: просмотр", "eye", 10,
        "Смотреть экран пользователя без управления", control: false);

/// <summary>Shadow: просмотр и управление мышью и клавиатурой сессии.</summary>
public sealed class ShadowControlAction()
    : ShadowAction("connection.shadow-control", "Shadow: управление", "mouse", 20,
        "Смотреть экран и управлять мышью и клавиатурой пользователя", control: true);
