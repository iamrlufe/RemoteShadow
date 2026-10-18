using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Modules;

/// <summary>Добавляет пользователя в группу, выбранную в диалоге поиска групп.</summary>
public sealed class AddToGroupAction : IAction<UserTarget>
{
    /// <summary>Имя параметра с DN выбранной группы.</summary>
    public const string GroupDnParameter = "groupDn";

    /// <summary>Имя параметра с названием выбранной группы (для журнала и сообщений).</summary>
    public const string GroupNameParameter = "groupName";

    private const int SearchLimit = 30;
    private const string DialogTitle = "Добавить в группу";

    public string Id => "users.add-to-group";
    public string Title => "Добавить в группу…";
    public string Group => UsersModule.GroupName;
    public string? Icon => "users-plus";
    public int Order => 50;
    public string? Description => "Добавить пользователя в группу AD";
    public ActionDanger Danger => ActionDanger.Confirm;
    public bool SupportsBulk => false;

    public bool CanExecute(UserTarget target, ActionContext context) => true;

    public async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IReadOnlyList<UserTarget> targets, ActionContext context, CancellationToken ct)
    {
        var group = await context.Dialogs.PickGroupAsync(DialogTitle,
            (text, searchCt) => context.Users.SearchGroupsAsync(text, SearchLimit, searchCt));
        return GroupParameters.From(group);
    }

    public async Task<ActionResult> ExecuteAsync(UserTarget target, ActionContext context, CancellationToken ct)
    {
        var (groupDn, groupName) = GroupParameters.Require(context);
        await context.Users.AddToGroupAsync(target.User.DistinguishedName, groupDn, ct);

        context.AuditDetails = $"группа: {groupName} ({groupDn})";
        return ActionResult.Ok($"Добавлен в группу «{groupName}»");
    }
}

/// <summary>Удаляет пользователя из одной из его групп (прямое членство).</summary>
public sealed class RemoveFromGroupAction : IAction<UserTarget>
{
    private const string DialogTitle = "Удалить из группы";

    public string Id => "users.remove-from-group";
    public string Title => "Удалить из группы…";
    public string Group => UsersModule.GroupName;
    public string? Icon => "users-minus";
    public int Order => 60;
    public string? Description => "Удалить пользователя из группы AD";
    public ActionDanger Danger => ActionDanger.ConfirmWithReason;
    public bool SupportsBulk => false;

    public bool CanExecute(UserTarget target, ActionContext context) => true;

    public async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IReadOnlyList<UserTarget> targets, ActionContext context, CancellationToken ct)
    {
        if (targets.Count == 0) return null;

        var groups = await context.Users.GetGroupsAsync(targets[0].User.DistinguishedName, ct);
        if (groups.Count == 0)
        {
            await context.Dialogs.ShowInfoAsync(DialogTitle, $"{targets[0].User.ShownName} не состоит ни в одной группе.");
            return null;
        }

        var group = await context.Dialogs.PickGroupAsync(DialogTitle,
            (text, _) => Task.FromResult(FilterGroups(groups, text)));
        return GroupParameters.From(group);
    }

    public async Task<ActionResult> ExecuteAsync(UserTarget target, ActionContext context, CancellationToken ct)
    {
        var (groupDn, groupName) = GroupParameters.Require(context);
        await context.Users.RemoveFromGroupAsync(target.User.DistinguishedName, groupDn, ct);

        context.AuditDetails = $"группа: {groupName} ({groupDn})";
        return ActionResult.Ok($"Удалён из группы «{groupName}»");
    }

    /// <summary>Локальный поиск по названию и описанию группы без учёта регистра; пустой текст — все группы.</summary>
    internal static IReadOnlyList<GroupInfo> FilterGroups(IReadOnlyList<GroupInfo> groups, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return groups;

        var needle = text.Trim();
        return groups
            .Where(g => g.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || (g.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }
}

/// <summary>Общая часть двух групповых действий: параметры «какая группа».</summary>
internal static class GroupParameters
{
    public static IReadOnlyDictionary<string, string>? From(GroupInfo? group) =>
        group is null
            ? null
            : new Dictionary<string, string>
            {
                [AddToGroupAction.GroupDnParameter] = group.DistinguishedName,
                [AddToGroupAction.GroupNameParameter] = group.Name,
            };

    public static (string Dn, string Name) Require(ActionContext context)
    {
        var dn = context.GetParameter(AddToGroupAction.GroupDnParameter);
        if (string.IsNullOrWhiteSpace(dn))
            throw new OperationFailedException("Не выбрана группа.", OperationFailure.InvalidInput);

        var name = context.GetParameter(AddToGroupAction.GroupNameParameter);
        return (dn, string.IsNullOrWhiteSpace(name) ? dn : name);
    }
}
