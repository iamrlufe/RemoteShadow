using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.Core.Services;
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
    private readonly WindowStateStore _stateStore;
    private readonly IDialogService _dialogs;
    private readonly MainWindowPlacement? _savedPlacement;

    public MainWindow(MainViewModel viewModel, WindowStateStore stateStore, IDialogService dialogs)
    {
        _viewModel = viewModel;
        _stateStore = stateStore;
        _dialogs = dialogs;
        DataContext = viewModel;
        InitializeComponent();

        ChangeAccountItem.Icon = IconMap.CreateIcon("user");
        SignOutItem.Icon = IconMap.CreateIcon("logout");
        ShortcutsItem.Icon = IconMap.CreateIcon("info-circle");

        // Ctrl+K: фокус в поле поиска и выделение текста, чтобы новый запрос заменил прежний.
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.K, ModifierKeys.Control));

        // F5 — обновить текущий раздел, Ctrl+F5 — полностью (список из AD), Ctrl+F — фильтр раздела,
        // Ctrl+1…Ctrl+9 — раздел по номеру в навигации.
        InputBindings.Add(new KeyBinding(new RelayCommand(() => RefreshCurrentPage(full: false)), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => RefreshCurrentPage(full: true)), Key.F5, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusPageFilter), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(ShowShortcuts), Key.F1, ModifierKeys.None));
        var openPage = new RelayCommand<string>(OpenPageByNumber);
        for (var i = 1; i <= 9; i++)
        {
            InputBindings.Add(new KeyBinding(openPage, Key.D0 + i, ModifierKeys.Control) { CommandParameter = i.ToString() });
            InputBindings.Add(new KeyBinding(openPage, Key.NumPad0 + i, ModifierKeys.Control) { CommandParameter = i.ToString() });
        }

        viewModel.Search.ResultChosen += OnResultChosen;

        _savedPlacement = stateStore.Load();
        RestorePlacement(_savedPlacement);
    }

    // ===== Положение окна и последний раздел =====

    private void RestorePlacement(MainWindowPlacement? placement)
    {
        if (placement is null) return;

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (WindowStateStore.FitToScreen(placement, screen, new Size(MinWidth, MinHeight)) is { } rect)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Width;
            Height = rect.Height;
        }

        if (placement.Maximized) WindowState = System.Windows.WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == System.Windows.WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;

        _stateStore.Save(new MainWindowPlacement(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == System.Windows.WindowState.Maximized,
            _viewModel.CurrentPageId));
    }

    // ===== Клавиши раздела =====

    private void RefreshCurrentPage(bool full)
    {
        if (_viewModel.CurrentView?.DataContext is IRefreshablePage page) page.RefreshPage(full);
    }

    /// <summary>Ctrl+цифра: открыть раздел и сразу поставить фокус в его поле поиска.</summary>
    private void OpenPageByNumber(string? number)
    {
        _viewModel.OpenPageByNumberCommand.Execute(number);
        Dispatcher.BeginInvoke(FocusPageFilter, DispatcherPriority.Input);
    }

    private void FocusPageFilter()
    {
        if (Search.IsOpen) Search.Close();
        if (_viewModel.CurrentView is IFilterablePage page) page.FocusFilter();
    }

    private SearchViewModel Search => _viewModel.Search;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNavState(hover: false);
        _viewModel.Initialize(_savedPlacement?.LastPageId);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Search.Close();
        SavePlacement();
        base.OnClosing(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Search.ResultChosen -= OnResultChosen;
        SecureClipboard.ClearPending();
        _viewModel.Dispose();
    }

    // ===== Боковое меню: закрепить / свернуть до значков =====

    private const double NavFullWidth = 232;
    private const double NavCollapsedWidth = 56;
    private bool _navPinned = true;

    private void OnNavPinClick(object sender, RoutedEventArgs e)
    {
        _navPinned = !_navPinned;
        ApplyNavState(hover: false);
    }

    private void OnNavMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_navPinned) ApplyNavState(hover: true);
    }

    private void OnNavMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_navPinned) ApplyNavState(hover: false);
    }

    private void ApplyNavState(bool hover)
    {
        var expanded = _navPinned || hover;
        NavColumn.Width = new GridLength(_navPinned ? NavFullWidth : NavCollapsedWidth);
        NavPanel.Width = expanded ? NavFullWidth : NavCollapsedWidth;

        // Свёрнутое меню: по центру столбца значков — «гамбургер»; раскрытое: справа булавка (акцентная, если закреплено).
        NavPinButton.Content = expanded ? "" : "";
        NavPinButton.HorizontalAlignment = expanded ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        NavPinButton.Margin = expanded ? new Thickness(0, 8, 10, 0) : new Thickness(0, 8, 0, 0);
        NavPinButton.SetResourceReference(Control.ForegroundProperty,
            _navPinned ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        NavPinButton.ToolTip = _navPinned ? "Свернуть меню" : "Закрепить меню";
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

    private void OnShortcutsClick(object sender, RoutedEventArgs e) => ShowShortcuts();

    // Через очередь диспетчера: меню успевает закрыться до показа модального окна.
    private void ShowShortcuts() =>
        Dispatcher.BeginInvoke(() => _ = _dialogs.ShowInfoAsync(KeyboardShortcuts.Title, KeyboardShortcuts.Text), DispatcherPriority.Background);

    // ===== Уведомления =====

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem toast }) toast.DismissCommand.Execute(null);
    }

    // Длинную ошибку не успевают дочитать за 8 секунд: под указателем уведомление не закрывается.
    private void OnToastMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem toast }) toast.StopTimer();
    }

    private void OnToastMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem toast }) toast.StartTimer();
    }
}
