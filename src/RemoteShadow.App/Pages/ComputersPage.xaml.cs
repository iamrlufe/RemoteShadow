using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;

namespace RemoteShadow.App.Pages;

public partial class ComputersPage : UserControl, IFilterablePage
{
    /// <summary>Сколько места по высоте оставить списку компьютеров (и строкам «Показано N из M»), когда раскрыта панель.</summary>
    private const double MinListHeight = 170;

    public ComputersPage()
    {
        InitializeComponent();

        // Правая кнопка или клавиша меню по строке: те же действия, что и кнопки карточки, плюс копирование.
        ActionMenuBuilder.Attach(ComputerList, () => ViewModel?.BuildComputerMenu());
        ActionMenuBuilder.Attach(SessionList, () => ViewModel?.BuildSessionMenu());

        SizeChanged += (_, _) => FitPanelsToHeight();
        LeftPanel.SizeChanged += (_, _) => RefitOpenPanel();
    }

    private ComputersPageViewModel? ViewModel => DataContext as ComputersPageViewModel;

    public void FocusFilter()
    {
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        FilterBox.SelectAll();
    }

    // Открытые вместе панели вытесняют список компьютеров: раскрывается только одна.
    private void OnFiltersExpanded(object sender, RoutedEventArgs e)
    {
        GroupsExpander.IsExpanded = false;
        DiscoveredExpander.IsExpanded = false;
        FitPanelsToHeight();
    }

    private void OnDiscoveredExpanded(object sender, RoutedEventArgs e)
    {
        GroupsExpander.IsExpanded = false;
        FiltersExpander.IsExpanded = false;
        FitPanelsToHeight();
    }

    private void OnGroupsExpanded(object sender, RoutedEventArgs e)
    {
        FiltersExpander.IsExpanded = false;
        DiscoveredExpander.IsExpanded = false;
        FitPanelsToHeight();
    }

    private void OnCollapseFilters(object sender, RoutedEventArgs e) => FiltersExpander.IsExpanded = false;

    private void OnCollapseGroups(object sender, RoutedEventArgs e) => GroupsExpander.IsExpanded = false;

    /// <summary>
    /// На невысоком окне раскрытая панель фильтров или групп (до 320 px) целиком вытесняла список компьютеров.
    /// Высота панелей ограничивается так, чтобы списку оставалось хотя бы несколько строк.
    /// </summary>
    private void FitPanelsToHeight()
    {
        if (!IsLoaded) return;
        RefitOpenPanel();

        // Раскрытие/сворачивание меняет раскладку уже после обработчика: повторяем, когда размеры устоятся.
        Dispatcher.BeginInvoke(new Action(RefitOpenPanel), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RefitOpenPanel()
    {
        if (!IsLoaded) return;
        FitPanel(GroupsExpander, GroupsScroll, 120, 280);
        FitPanel(FiltersExpander, FiltersScroll, 120, 320);
        FitPanel(DiscoveredExpander, DiscoveredScroll, 140, 360);
    }

    /// <summary>Высота раскрытой панели подбирается по месту, реально оставшемуся под списком компьютеров: список всегда виден.</summary>
    private void FitPanel(Expander expander, ScrollViewer scroll, double min, double max)
    {
        if (!expander.IsExpanded || !expander.IsVisible) { scroll.MaxHeight = max; return; }

        var listTop = ComputerList.TranslatePoint(new Point(0, 0), LeftPanel).Y;
        var freeForList = LeftPanel.ActualHeight - listTop;
        var target = scroll.ActualHeight + (freeForList - MinListHeight);
        var clamped = Math.Clamp(target, min, max);
        if (Math.Abs(scroll.MaxHeight - clamped) > 1) scroll.MaxHeight = clamped;
    }

    // ----- поиск --------------------------------------------------------------------------------------------------

    /// <summary>Esc очищает поиск, стрелка вниз переводит фокус в список компьютеров.</summary>
    private void OnFilterBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when FilterBox.Text.Length > 0:
                FilterBox.Clear();
                e.Handled = true;
                break;

            case Key.Down when ComputerList.Items.Count > 0:
                FocusList(ComputerList);
                e.Handled = true;
                break;
        }
    }

    // ----- список компьютеров -------------------------------------------------------------------------------------

    private void OnComputerListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key == Key.Enter && vm.SelectedComputer is not null)
        {
            vm.RunComputerMainActionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.CopyComputerNameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && vm.Sessions.Count > 0)
        {
            // Стрелка вправо — к сессиям выбранного компьютера (там Enter запускает shadow).
            FocusList(SessionList);
            e.Handled = true;
        }
    }

    private void OnComputerListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !IsOnItem(e.OriginalSource)) return;

        ViewModel?.RunComputerMainActionCommand.Execute(null);
        e.Handled = true;
    }

    // ----- список сессий ------------------------------------------------------------------------------------------

    private void OnSessionListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key == Key.Enter && vm.SelectedSession is not null)
        {
            vm.RunSessionMainActionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.CopySessionUserCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            FocusList(ComputerList);
            e.Handled = true;
        }
    }

    private void OnSessionListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !IsOnItem(e.OriginalSource)) return;

        ViewModel?.RunSessionMainActionCommand.Execute(null);
        e.Handled = true;
    }

    // ----- группы -------------------------------------------------------------------------------------------------

    private void OnGroupsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel is not { SelectedGroup: { } group } vm) return;

        vm.DeleteGroupCommand.Execute(group);
        e.Handled = true;
    }

    // ----- общее --------------------------------------------------------------------------------------------------

    /// <summary>Двойной щелчок по полосе прокрутки или пустому месту списка не должен запускать подключение.</summary>
    private static bool IsOnItem(object source) => ListFocus.IsOnItem(source);

    private static void FocusList(ListBox list) => ListFocus.FocusSelectedOrFirst(list);
}
