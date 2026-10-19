using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.App.Shell.Converters;
using RemoteShadow.App.Shell.Search;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Модель главного окна: навигация, строка состояния, меню учётной записи. Страницы показывает
/// <see cref="NavigationService"/>, подключением и индексом управляет <see cref="ConnectionCoordinator"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>События индекса идут пачками (каждая страница, каждый статус «в сети»): строка состояния обновляется не чаще.</summary>
    private static readonly TimeSpan IndexRefreshInterval = TimeSpan.FromMilliseconds(300);

    private readonly NavigationService _navigation;
    private readonly ICurrentConnection _connection;
    private readonly IComputerIndex _index;
    private readonly ConnectionCoordinator _coordinator;
    private readonly INotificationService _notifications;
    private readonly DispatcherTimer _indexTimer;

    private int _indexRefreshPending;
    private bool _syncingSelection;
    private string _onlineText = string.Empty;
    private bool _disposed;

    public MainViewModel(
        NavigationService navigation,
        ICurrentConnection connection,
        IComputerIndex index,
        ConnectionCoordinator coordinator,
        NotificationService notifications,
        SearchViewModel search,
        StartupOptions options)
    {
        _navigation = navigation;
        _connection = connection;
        _index = index;
        _coordinator = coordinator;
        _notifications = notifications;

        Search = search;
        Toasts = notifications.Toasts;
        IsDemo = options.IsDemo;
        Title = options.IsDemo ? "RemoteShadow — демо" : "RemoteShadow";
        NavItems = navigation.Pages.Select(p => new NavItem(p)).ToList();

        _indexTimer = new DispatcherTimer { Interval = IndexRefreshInterval };
        _indexTimer.Tick += OnIndexTimerTick;

        _navigation.Navigated += OnNavigated;
        _connection.Changed += OnConnectionChanged;
        _index.Changed += OnIndexChanged;
        _coordinator.IndexStartErrorChanged += OnIndexStartErrorChanged;

        UpdateConnectionStatus();
        UpdateIndexStatus();
        SyncNavigation();
    }

    public string Title { get; }

    public bool IsDemo { get; }

    public SearchViewModel Search { get; }

    /// <summary>Всплывающие уведомления (общая коллекция <see cref="NotificationService"/>).</summary>
    public ObservableCollection<ToastItem> Toasts { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Есть ли хоть один раздел: иначе окно показывает «Нет доступных разделов».</summary>
    public bool HasPages => NavItems.Count > 0;

    /// <summary>Пункт навигации. Выбор пользователем открывает страницу; переходы из кода только отражаются здесь.</summary>
    public NavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (!SetProperty(ref _selectedNavItem, value) || _syncingSelection || value is null) return;

            _navigation.NavigateTo(value.Id);

            // Страница не открылась (ошибка при создании): выбор в списке возвращается к показанной странице.
            if (!string.Equals(_navigation.CurrentPageId, value.Id, StringComparison.OrdinalIgnoreCase))
                UiThread.Post(SyncNavigation);
        }
    }

    private NavItem? _selectedNavItem;

    /// <summary>Представление текущей страницы.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentView))]
    private FrameworkElement? _currentView;

    public bool HasCurrentView => CurrentView is not null;

    /// <summary>Текст кнопки учётной записи: «CORP\adm-ivanov».</summary>
    [ObservableProperty]
    private string _accountLabel = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    /// <summary>Строка состояния слева: «CORP\adm-ivanov · DC01.corp.local · Kerberos · в сети 182 из 230».</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Строка состояния справа: «Обновлено в 10:42», «Идёт загрузка…» или текст ошибки.</summary>
    [ObservableProperty]
    private string _indexText = string.Empty;

    [ObservableProperty]
    private bool _indexIsError;

    /// <summary>
    /// Открывает раздел, на котором закончили в прошлый раз (<paramref name="lastPageId"/>), а если его нет — первую
    /// страницу (предпочтительно «Компьютеры»). Вызывается, когда окно уже показано.
    /// </summary>
    public void Initialize(string? lastPageId = null)
    {
        if (_navigation.CurrentPageId is not null || _navigation.Pages.Count == 0) return;

        var first = _navigation.Pages.FirstOrDefault(p => string.Equals(p.Page.Id, lastPageId, StringComparison.OrdinalIgnoreCase))
                    ?? _navigation.Pages.FirstOrDefault(p => p.Page.Id == PageIds.Computers)
                    ?? _navigation.Pages[0];
        _navigation.NavigateTo(first.Page.Id);
    }

    /// <summary>Id открытого раздела — запоминается при закрытии окна.</summary>
    public string? CurrentPageId => _navigation.CurrentPageId;

    /// <summary>Ctrl+1…Ctrl+9: открыть раздел по номеру в навигации.</summary>
    [RelayCommand]
    private void OpenPageByNumber(string? number)
    {
        if (!int.TryParse(number, out var n) || n < 1 || n > NavItems.Count) return;
        SelectedNavItem = NavItems[n - 1];
    }

    /// <summary>«Сменить учётную запись…»: окно входа поверх главного; при успехе страницы перезагрузятся по событию подключения.</summary>
    [RelayCommand]
    private void ChangeAccount()
    {
        if (!_coordinator.ShowLogin(Application.Current?.MainWindow, exitsApp: false)) return;

        _notifications.Success($"Выполнен вход: {_connection.Current?.AccountName}");
    }

    /// <summary>«Выйти»: подключение освобождается; затем окно входа, а если его закрыли — выход из программы.</summary>
    [RelayCommand]
    private void SignOut()
    {
        _coordinator.SignOut();

        if (_coordinator.ShowLogin(Application.Current?.MainWindow, exitsApp: true))
            _notifications.Success($"Выполнен вход: {_connection.Current?.AccountName}");
        else
            Application.Current?.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _indexTimer.Stop();
        _navigation.Navigated -= OnNavigated;
        _connection.Changed -= OnConnectionChanged;
        _index.Changed -= OnIndexChanged;
        _coordinator.IndexStartErrorChanged -= OnIndexStartErrorChanged;
    }

    private void OnNavigated(object? sender, NavigationEventArgs e) => SyncNavigation();

    private void SyncNavigation()
    {
        CurrentView = _navigation.CurrentView;

        _syncingSelection = true;
        try
        {
            SelectedNavItem = NavItems.FirstOrDefault(i => string.Equals(i.Id, _navigation.CurrentPageId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnConnectionChanged(object? sender, EventArgs e) =>
        UiThread.Post(() =>
        {
            if (_disposed) return;
            UpdateConnectionStatus();
        });

    private void OnIndexStartErrorChanged(object? sender, EventArgs e) =>
        UiThread.Post(() =>
        {
            if (!_disposed) UpdateIndexStatus();
        });

    // Может прийти из любого потока: в очереди интерфейса остаётся не больше одного запроса на обновление.
    private void OnIndexChanged(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _indexRefreshPending, 1, 0) != 0) return;

        UiThread.Post(() =>
        {
            if (_disposed) return;
            if (!_indexTimer.IsEnabled) _indexTimer.Start();
        });
    }

    private void OnIndexTimerTick(object? sender, EventArgs e)
    {
        _indexTimer.Stop();
        Volatile.Write(ref _indexRefreshPending, 0);
        UpdateIndexStatus();
    }

    private void UpdateConnectionStatus()
    {
        var connection = _connection.Current;
        IsLoggedIn = connection is not null;
        AccountLabel = connection?.AccountName ?? "Не выполнен вход";
        UpdateStatusText();
    }

    private void UpdateIndexStatus()
    {
        _onlineText = FormatOnline(_index.All);

        var error = _index.LastError;
        if (string.IsNullOrWhiteSpace(error)) error = _coordinator.IndexStartError;

        if (_index.IsLoading)
        {
            IndexText = "Идёт загрузка списка компьютеров…";
            IndexIsError = false;
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            IndexText = $"Ошибка: {error}";
            IndexIsError = true;
        }
        else if (_index.LastSyncUtc is { } synced)
        {
            IndexText = $"Обновлено в {RelativeTimeFormatter.ToUtc(synced).ToLocalTime():HH:mm}";
            IndexIsError = false;
        }
        else
        {
            IndexText = _connection.Current is null ? string.Empty : "Список компьютеров не загружен";
            IndexIsError = false;
        }

        UpdateStatusText();
    }

    /// <summary>«в сети 182 из 230». Пока статус «в сети» ещё ни у кого не проверен, число не показывается: «0 из 230» вводило бы в заблуждение.</summary>
    internal static string FormatOnline(IReadOnlyList<ComputerInfo> computers)
    {
        if (computers.Count == 0) return string.Empty;
        if (!computers.Any(c => c.IsOnline.HasValue)) return $"компьютеров: {computers.Count}";

        return $"в сети {computers.Count(c => c.IsOnline == true)} из {computers.Count}";
    }

    private void UpdateStatusText()
    {
        var parts = new List<string>();
        if (_connection.Current is { } connection)
        {
            parts.Add(connection.AccountName);
            parts.Add(string.IsNullOrWhiteSpace(connection.DomainController) ? connection.DomainName : connection.DomainController);
            parts.Add(connection.AuthenticationKind);
            if (_onlineText.Length > 0) parts.Add(_onlineText);
        }
        else
        {
            parts.Add("Не выполнен вход в домен");
        }

        StatusText = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
