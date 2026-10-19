using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;

namespace RemoteShadow.App.Pages;

public partial class ComputersPage : UserControl, IFilterablePage
{
    public ComputersPage()
    {
        InitializeComponent();

        // Правая кнопка или клавиша меню по строке: те же действия, что и кнопки карточки, плюс копирование.
        ActionMenuBuilder.Attach(ComputerList, () => ViewModel?.BuildComputerMenu());
        ActionMenuBuilder.Attach(SessionList, () => ViewModel?.BuildSessionMenu());

        TabList.IsChecked = true;
        DataContextChanged += OnDataContextChanged;
    }

    private ComputersPageViewModel? ViewModel => DataContext as ComputersPageViewModel;

    public void FocusFilter()
    {
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        FilterBox.SelectAll();
    }

    // ----- вкладки колонки ----------------------------------------------------------------------------------------

    /// <summary>Колонка слева показывает одну вкладку целиком: список компьютеров или одну из панелей. Так панели не вытесняют список и не прокручиваются внутри прокрутки.</summary>
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || ComputerList is null) return;

        ComputerList.Visibility = tag == "list" ? Visibility.Visible : Visibility.Collapsed;
        GroupsScroll.Visibility = tag == "groups" ? Visibility.Visible : Visibility.Collapsed;
        SubnetsScroll.Visibility = tag == "subnets" ? Visibility.Visible : Visibility.Collapsed;
        FiltersScroll.Visibility = tag == "filters" ? Visibility.Visible : Visibility.Collapsed;
        DiscoveredScroll.Visibility = tag == "found" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ComputersPageViewModel old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
            old.ScanFinished -= OnScanFinished;
        }

        if (e.NewValue is ComputersPageViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.ScanFinished += OnScanFinished;
        }
    }

    // Переход из поиска Ctrl+K или из «Избранного» выбирает компьютер: показываем список, где он выделен.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComputersPageViewModel.SelectedComputer) && ViewModel?.SelectedComputer is not null && TabList.IsChecked != true)
            TabList.IsChecked = true;
    }

    // Скан закончился, пока пользователь на вкладке «Подсети»: сразу показываем найденные устройства, а не ждём, что он их найдёт сам.
    private void OnScanFinished(object? sender, EventArgs e)
    {
        if (TabSubnets.IsChecked == true) TabFound.IsChecked = true;
    }

    /// <summary>Вложенный список без собственной прокрутки не должен глотать колесо мыши: отдаём его объемлющей панели.</summary>
    private void OnNestedListWheel(object sender, MouseWheelEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d is not null && d is not ScrollViewer) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is not ScrollViewer scroll) return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
        e.Handled = true;
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
