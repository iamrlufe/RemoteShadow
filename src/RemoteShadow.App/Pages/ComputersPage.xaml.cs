using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;

namespace RemoteShadow.App.Pages;

public partial class ComputersPage : UserControl, IFilterablePage
{
    /// <summary>Сколько места по высоте оставить списку компьютеров (и строкам «Показано N из M»), когда раскрыта панель.</summary>
    private const double ReservedForList = 200;

    /// <summary>Заголовок свёрнутой панели (Expander) и поле поиска между панелями.</summary>
    private const double ExpanderHeaderHeight = 52;
    private const double SearchBoxHeight = 44;

    public ComputersPage()
    {
        InitializeComponent();

        // Правая кнопка или клавиша меню по строке: те же действия, что и кнопки карточки, плюс копирование.
        ActionMenuBuilder.Attach(ComputerList, () => ViewModel?.BuildComputerMenu());
        ActionMenuBuilder.Attach(SessionList, () => ViewModel?.BuildSessionMenu());

        SizeChanged += (_, _) => FitPanelsToHeight();
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
        FitPanelsToHeight();
    }

    private void OnGroupsExpanded(object sender, RoutedEventArgs e)
    {
        FiltersExpander.IsExpanded = false;
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

        // Над панелями — заголовок и кнопки (их высота зависит от ширины: кнопки переносятся), поэтому берём фактическое положение.
        var groupsTop = GroupsExpander.TranslatePoint(new Point(0, 0), this).Y;
        var forGroups = ActualHeight - groupsTop - ExpanderHeaderHeight - SearchBoxHeight - ExpanderHeaderHeight - ReservedForList;
        GroupsScroll.MaxHeight = Math.Clamp(forGroups, 120, 280);

        var filtersTop = FiltersExpander.TranslatePoint(new Point(0, 0), this).Y;
        var forFilters = ActualHeight - filtersTop - ExpanderHeaderHeight - ReservedForList;
        FiltersScroll.MaxHeight = Math.Clamp(forFilters, 120, 320);
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
