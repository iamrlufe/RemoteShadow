using CommunityToolkit.Mvvm.Input;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell.Actions;

/// <summary>
/// Вспомогательные операции над готовыми кнопками действий: «главное действие» строки (двойной щелчок, Enter) и
/// дополнительные пункты контекстного меню (копирование, переходы), которые не являются действиями модулей.
/// </summary>
public static class ActionButtons
{
    /// <summary>Группа служебных пунктов меню: копирование имени, логина и т. п.</summary>
    public const string CopyGroup = "Копировать";

    /// <summary>Группа переходов в другие разделы.</summary>
    public const string NavigateGroup = "Перейти";

    /// <summary>
    /// Выполняет первую кнопку из <paramref name="preferredIds"/>, которая есть в списке и доступна. Если подходящая
    /// кнопка есть, но выключена, — сообщает причину. true — действие запущено.
    /// </summary>
    public static bool ExecuteFirst(IReadOnlyList<ActionButtonModel> buttons, INotificationService notifications, params string[] preferredIds)
    {
        ActionButtonModel? disabled = null;
        foreach (var id in preferredIds)
        {
            var button = buttons.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
            if (button is null) continue;

            if (button.IsEnabled && button.Command.CanExecute(null))
            {
                button.Command.Execute(null);
                return true;
            }

            disabled ??= button;
        }

        if (disabled is not null)
            notifications.Info(disabled.DisabledReason is { Length: > 0 } reason ? $"«{disabled.Title}» недоступно: {reason}" : $"«{disabled.Title}» сейчас недоступно.");

        return false;
    }

    /// <summary>Пункт меню, не связанный с модулями (копирование, переход): всегда доступен, выполняется сразу.</summary>
    public static ActionButtonModel Local(string id, string title, string group, string? icon, Action execute, string? description = null) =>
        new(id, title, group, icon, description, true, null, new RelayCommand(execute));
}
