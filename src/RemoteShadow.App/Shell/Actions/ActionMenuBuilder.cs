using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RemoteShadow.App.Shell.Actions;

/// <summary>
/// Строит контекстное меню из тех же <see cref="ActionButtonModel"/>, что и <see cref="ActionBar"/>: пункты
/// сгруппированы (между группами разделитель), недоступные выключены с причиной в подсказке.
/// </summary>
public static class ActionMenuBuilder
{
    /// <summary>Новое меню для списка кнопок. Пустой список даёт меню с одним выключенным пунктом.</summary>
    public static ContextMenu Build(IReadOnlyList<ActionButtonModel> actions)
    {
        var menu = new ContextMenu();
        string? previousGroup = null;

        foreach (var action in actions)
        {
            if (previousGroup is not null && !string.Equals(previousGroup, action.Group, StringComparison.Ordinal))
                menu.Items.Add(new Separator());

            menu.Items.Add(CreateItem(action));
            previousGroup = action.Group;
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Нет доступных действий", IsEnabled = false });

        return menu;
    }

    /// <summary>
    /// Подключает меню к элементу (строке списка, карточке): при правом щелчке или клавише меню оно строится заново из
    /// текущего результата <paramref name="provider"/>, так что всегда соответствует выделению. Если провайдер вернул
    /// null или пустой список, меню не показывается.
    /// </summary>
    public static void Attach(FrameworkElement element, Func<IReadOnlyList<ActionButtonModel>?> provider)
    {
        element.ContextMenuOpening += (_, e) =>
        {
            e.Handled = true;

            var actions = provider();
            if (actions is null || actions.Count == 0) return;

            var menu = Build(actions);
            menu.PlacementTarget = element;
            menu.Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
            menu.IsOpen = true;
        };
    }

    private static MenuItem CreateItem(ActionButtonModel action)
    {
        var item = new MenuItem
        {
            Header = action.Title,
            Command = action.Command,
            IsEnabled = action.IsEnabled,
            ToolTip = action.ToolTipText,
        };

        if (action.HasIcon) item.Icon = IconMap.CreateIcon(action.Icon);

        ToolTipService.SetShowOnDisabled(item, true);
        AutomationProperties.SetName(item, action.Title);
        return item;
    }
}
