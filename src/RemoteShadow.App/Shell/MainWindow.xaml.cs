using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.App.Shell.Dialogs;
using RemoteShadow.App.Shell.Search;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Главное окно: верхняя строка с поиском и учётной записью, навигация слева, страница в центре, строка состояния
/// и уведомления. Логика — в <see cref="MainViewModel"/> и <see cref="SearchViewModel"/>; здесь только фокус,
/// клавиатура и всплывающая панель поиска.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ChangeAccountItem.Icon = IconMap.CreateIcon("user");
        SignOutItem.Icon = IconMap.CreateIcon("logout");

        // Ctrl+K: фокус в поле поиска и выделение текста, чтобы новый запрос заменил прежний.
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.K, ModifierKeys.Control));
        viewModel.Search.ResultChosen += OnResultChosen;
    }

    private SearchViewModel Search => _viewModel.Search;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Search.Close();
        base.OnClosing(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Search.ResultChosen -= OnResultChosen;
        SecureClipboard.ClearPending();
        _viewModel.Dispose();
    }

    // ===== Поиск =====

    private void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    private void OnSearchGotFocus(object sender, KeyboardFocusChangedEventArgs e) => Search.SetInputFocused(true);

    private void OnSearchLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Search.SetInputFocused(false);

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (!Search.IsOpen) Search.SetInputFocused(true);
                else Search.MoveSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;

            case Key.Enter:
                if (Search.IsOpen) Search.CommitSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                if (!Search.Dismiss()) MoveFocusToContent();
                e.Handled = true;
                break;
        }
    }

    private void OnSearchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchList.SelectedItem is { } selected) SearchList.ScrollIntoView(selected);
    }

    private void OnSearchItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SearchItem item }) return;

        e.Handled = true;
        Search.ChooseCommand.Execute(item);
    }

    private void OnSearchItemMouseEnter(object sender, MouseEventArgs e)
    {
        // Подсветка следует за мышью, чтобы выбранной оставалась та строка, которую откроет Enter.
        if (sender is ListBoxItem { DataContext: SearchItem { IsSelectable: true } item }) Search.SelectedItem = item;
    }

    private void OnResultChosen(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(MoveFocusToContent, DispatcherPriority.Input);

    private void MoveFocusToContent()
    {
        Keyboard.ClearFocus();
        PageHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Search.IsOpen) return;
        if (e.OriginalSource is DependencyObject source && (IsInside(source, SearchHost) || IsInside(source, SearchPopupRoot))) return;

        Search.Close();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => Search.Close();

    private void OnWindowMoved(object? sender, EventArgs e)
    {
        if (Search.IsOpen) Search.Close();
    }

    private static bool IsInside(DependencyObject? node, DependencyObject container)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, container)) return true;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    // ===== Учётная запись =====

    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        AccountMenu.PlacementTarget = AccountButton;
        AccountMenu.Placement = PlacementMode.Bottom;
        AccountMenu.IsOpen = true;
    }

    private void OnAccountMenuOpened(object sender, RoutedEventArgs e) => SignOutItem.IsEnabled = _viewModel.IsLoggedIn;

    private void OnChangeAccountClick(object sender, RoutedEventArgs e) => _viewModel.ChangeAccountCommand.Execute(null);

    private void OnSignOutClick(object sender, RoutedEventArgs e) => _viewModel.SignOutCommand.Execute(null);

    // ===== Уведомления =====

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem toast }) toast.DismissCommand.Execute(null);
    }
}
