using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RemoteShadow.App.Shell.Actions;

namespace RemoteShadow.App.Pages;

public partial class FavoritesPage : UserControl
{
    public FavoritesPage()
    {
        InitializeComponent();
    }

    /// <summary>Правая кнопка по карточке компьютера: меню из тех же действий, что и кнопки строки, плюс переходы и копирование.</summary>
    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: FavoriteComputerItem item } row || DataContext is not FavoritesPageViewModel vm) return;

        var menu = ActionMenuBuilder.Build(vm.BuildRowMenu(item));
        menu.PlacementTarget = row;
        menu.Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
}
