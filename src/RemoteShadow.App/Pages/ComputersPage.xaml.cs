using System.Windows;
using System.Windows.Controls;

namespace RemoteShadow.App.Pages;

public partial class ComputersPage : UserControl
{
    public ComputersPage()
    {
        InitializeComponent();
    }

    // Открытые вместе панели вытесняют список компьютеров: раскрывается только одна.
    private void OnFiltersExpanded(object sender, RoutedEventArgs e) => GroupsExpander.IsExpanded = false;

    private void OnGroupsExpanded(object sender, RoutedEventArgs e) => FiltersExpander.IsExpanded = false;

    private void OnCollapseFilters(object sender, RoutedEventArgs e) => FiltersExpander.IsExpanded = false;

    private void OnCollapseGroups(object sender, RoutedEventArgs e) => GroupsExpander.IsExpanded = false;
}
