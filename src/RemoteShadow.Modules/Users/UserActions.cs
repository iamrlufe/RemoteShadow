using RemoteShadow.Core;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>
/// Сброс пароля. Пароль вводится в диалоге (<see cref="PrepareAsync"/>) и живёт только в параметрах действия:
/// ни в <see cref="ActionResult.Message"/>, ни в <see cref="ActionContext.AuditDetails"/>, ни в исключениях его нет.
/// </summary>
public sealed class ResetPasswordAction : IAction<UserTarget>
{
    /// <summary>Имя параметра с новым паролем.</summary>
    public const string PasswordParameter = "password";

    /// <summary>Имя параметра «сменить при следующем входе»: "true" или "false".</summary>
    public const string MustChangeParameter = "mustChange";

    public string Id => "users.reset-password";
    public string Title => "Сбросить пароль";
    public string Group => UsersModule.GroupName;
    public string? Icon => "key";
    public int Order => 10;
    public string? Description => "Задать пользователю новый пароль";
    public ActionDanger Danger => ActionDanger.ConfirmWithReason;
    public bool SupportsBulk => false;

    public bool CanExecute(UserTarget target, ActionContext context) => true;

    public async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IReadOnlyList<UserTarget> targets, ActionContext context, CancellationToken ct)
    {
        var user = targets.Count > 0 ? targets[0].User.ShownName : string.Empty;

        var choice = await context.Dialogs.PromptPasswordResetAsync(user);
        if (choice is null) return null;

        if (string.IsNullOrEmpty(choice.Password))
            throw new OperationFailedException("Пароль не может быть пустым.", OperationFailure.InvalidInput);

        return new Dictionary<string, string>
        {
            [PasswordParameter] = choice.Password,
            [MustChangeParameter] = choice.MustChangeAtNextLogon ? "true" : "false",
        };
    }

    public async Task<ActionResult> ExecuteAsync(UserTarget target, ActionContext context, CancellationToken ct)
    {
        var password = context.GetParameter(PasswordParameter);
        if (string.IsNullOrEmpty(password))
            throw new OperationFailedException("Не задан новый пароль.", OperationFailure.InvalidInput);

        // Если параметра нет, безопаснее потребовать смену пароля: временный пароль не должен остаться постоянным.
        var mustChange = !string.Equals(context.GetParameter(MustChangeParameter), "false", StringComparison.OrdinalIgnoreCase);
        await context.Users.ResetPasswordAsync(target.User.DistinguishedName, password, mustChange, ct);

        context.AuditDetails = $"сменить при следующем входе: {(mustChange ? "да" : "нет")}";
        return ActionResult.Ok("Пароль сброшен");
    }
}

/// <summary>Снимает блокировку учётной записи (lockoutTime = 0).</summary>
public sealed class UnlockAction : IAction<UserTarget>
{
    public string Id => "users.unlock";
    public string Title => "Разблокировать";
    public string Group => UsersModule.GroupName;
    public string? Icon => "lock-open";
    public int Order => 20;
    public string? Description => "Снять блокировку учётной записи";
    public ActionDanger Danger => ActionDanger.Confirm;
    public bool SupportsBulk => true;

    public bool CanExecute(UserTarget target, ActionContext context) => target.User.LockedOut;

    public string? GetUnavailableReason(UserTarget target, ActionContext context) =>
        target.User.LockedOut ? null : "Учётная запись не заблокирована";

    public async Task<ActionResult> ExecuteAsync(UserTarget target, ActionContext context, CancellationToken ct)
    {
        await context.Users.UnlockAsync(target.User.DistinguishedName, ct);
        return ActionResult.Ok("Учётная запись разблокирована");
    }
}

/// <summary>Включает отключённую учётную запись.</summary>
public sealed class EnableAction : IAction<UserTarget>
{
    public string Id => "users.enable";
    public string Title => "Включить";
    public string Group => UsersModule.GroupName;
    public string? Icon => "user-check";
    public int Order => 30;
    public string? Description => "Включить отключённую учётную запись";
    public ActionDanger Danger => ActionDanger.Confirm;
    public bool SupportsBulk => true;

    public bool CanExecute(UserTarget target, ActionContext context) => !target.User.Enabled;

    public string? GetUnavailableReason(UserTarget target, ActionContext context) =>
        target.User.Enabled ? "Учётная запись уже включена" : null;

    public async Task<ActionResult> ExecuteAsync(UserTarget target, ActionContext context, CancellationToken ct)
    {
        await context.Users.SetEnabledAsync(target.User.DistinguishedName, true, ct);
        return ActionResult.Ok("Учётная запись включена");
    }
}

/// <summary>Отключает учётную запись: пользователь не сможет войти.</summary>
public sealed class DisableAction : IAction<UserTarget>
{
    public string Id => "users.disable";
    public string Title => "Отключить";
    public string Group => UsersModule.GroupName;
    public string? Icon => "user-off";
    public int Order => 40;
    public string? Description => "Отключить учётную запись: войти будет нельзя";
    public ActionDanger Danger => ActionDanger.ConfirmWithReason;
    public bool SupportsBulk => true;

    public bool CanExecute(UserTarget target, ActionContext context) => target.User.Enabled;

    public string? GetUnavailableReason(UserTarget target, ActionContext context) =>
        target.User.Enabled ? null : "Учётная запись уже отключена";

    public async Task<ActionResult> ExecuteAsync(UserTarget target, ActionContext context, CancellationToken ct)
    {
        await context.Users.SetEnabledAsync(target.User.DistinguishedName, false, ct);
        return ActionResult.Ok("Учётная запись отключена");
    }
}
