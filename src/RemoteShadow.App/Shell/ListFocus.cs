using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RemoteShadow.App.Shell;

/// <summary>Общие приёмы клавиатуры и мыши для списков страниц (двойной щелчок по строке, переход фокуса в список).</summary>
public static class ListFocus
{
    /// <summary>true — щелчок пришёлся на строку списка, а не на полосу прокрутки или пустое место.</summary>
    public static bool IsOnItem(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = GetParent(node))
        {
            if (node is ListBoxItem) return true;
            if (node is ScrollBar or ListBox) return false;
        }

        return false;
    }

    /// <summary>Фокус на выбранную (или первую) строку, чтобы стрелки и Enter сразу работали.</summary>
    public static void FocusSelectedOrFirst(ListBox list)
    {
        if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;

        list.UpdateLayout();
        if (list.SelectedIndex >= 0) list.ScrollIntoView(list.SelectedItem);
        if (list.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) is ListBoxItem item) item.Focus();
        else list.Focus();
    }

    // Run и другие элементы текста не входят в визуальное дерево: для них идём по логическому.
    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
