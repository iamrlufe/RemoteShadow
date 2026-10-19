using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Pages;

/// <summary>
/// Строка списка «Избранные» или «Недавние»: компьютер, его избранность и быстрые действия (RDP, SSH).
/// Кнопки строит собственный <see cref="ActionPresenter{TTarget}"/> строки, поэтому ожидание одного действия
/// не блокирует остальные строки.
/// </summary>
public sealed partial class FavoriteComputerItem : ObservableObject
{
    /// <summary>Id быстрых действий, которые показываются в строке (остальные доступны на странице «Компьютеры»).</summary>
    internal static readonly string[] QuickActionIds = ["connection.rdp", "connection.ssh"];

    public FavoriteComputerItem(ComputerInfo computer, bool isFavorite, ActionPresenter<ComputerTarget> presenter)
    {
        Computer = computer;
        IsFavorite = isFavorite;
        Presenter = presenter;
        Buttons = presenter
            .BuildButtons(new[] { new ComputerTarget(computer) }, null)
            .Where(b => QuickActionIds.Contains(b.Id))
            .ToList();
    }

    public ComputerInfo Computer { get; }

    public bool IsFavorite { get; }

    public ActionPresenter<ComputerTarget> Presenter { get; }

    public IReadOnlyList<ActionButtonModel> Buttons { get; }

    public Guid Id => Computer.Id;

    public string Name => Computer.Name;

    public bool? IsOnline => Computer.IsOnline;

    public string StatusText => Computer.IsOnline switch
    {
        true => "в сети",
        false => "не в сети",
        _ => "не проверялся",
    };

    /// <summary>Вторая строка: ОС и описание — то, что отличает компьютеры с похожими именами.</summary>
    public string Details
    {
        get
        {
            var parts = new[] { Computer.OperatingSystem, Computer.Description }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Страница «Избранное»: списки «Избранные» (<see cref="AppSettings.FavoriteComputers"/>) и «Недавние»
/// (<see cref="AppSettings.RecentComputers"/>). Компьютеры берутся из <see cref="IComputerIndex"/>; статус «в сети»
/// обновляет <see cref="IOnlineStatusService"/> (списки короткие — проверяются сразу при открытии страницы и по кнопке).
/// </summary>
public sealed partial class FavoritesPageViewModel : ObservableObject, INavigationAware, IDisposable
{
    private static readonly TimeSpan ChangeInterval = TimeSpan.FromMilliseconds(250);

    private readonly IComputerIndex _index;
    private readonly IOnlineStatusService _online;
    private readonly IActionRunner _runner;
    private readonly INotificationService _notifications;
    private readonly ISettingsStore _settings;
    private readonly INavigationService _navigation;

    private CancellationTokenSource? _onlineCts;
    private int _changePending;
    private bool _active;
    private bool _disposed;

    public FavoritesPageViewModel(
        IComputerIndex index,
        IOnlineStatusService online,
        IActionRunner runner,
        INotificationService notifications,
        ISettingsStore settings,
        INavigationService navigation)
    {
        _index = index;
        _online = online;
        _runner = runner;
        _notifications = notifications;
        _settings = settings;
        _navigation = navigation;

        _index.Changed += OnIndexChanged;
        _navigation.Navigated += OnNavigated;
        Reload();
    }

    public ObservableCollection<FavoriteComputerItem> Favorites { get; } = new();

    public ObservableCollection<FavoriteComputerItem> Recent { get; } = new();

    public bool HasFavorites => Favorites.Count > 0;

    public bool HasRecent => Recent.Count > 0;

    /// <summary>Пусто везде: показываем пояснение вместо двух пустых списков.</summary>
    public bool IsEmpty => !HasFavorites && !HasRecent;

    /// <summary>Сколько сохранённых компьютеров не найдено в индексе (удалены из AD или индекс ещё грузится).</summary>
    [ObservableProperty]
    private int _missingCount;

    [ObservableProperty]
    private bool _isChecking;

    public string? MissingText => MissingCount == 0
        ? null
        : $"Не найдено в индексе: {MissingCount}. Возможно, компьютеры удалены из домена или индекс ещё загружается.";

    partial void OnMissingCountChanged(int value) => OnPropertyChanged(nameof(MissingText));

    public void OnNavigatedTo(object? parameter)
    {
        _active = true;
        Reload();
        _ = CheckOnlineAsync(force: false);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Reload();
        await CheckOnlineAsync(force: true);
    }

    [RelayCommand]
    private void Open(FavoriteComputerItem? item)
    {
        if (item is null) return;
        _navigation.NavigateTo(PageIds.Computers, new SelectComputerRequest(item.Id));
    }

    [RelayCommand]
    private void RemoveFavorite(FavoriteComputerItem? item) => SetFavorite(item, false);

    [RelayCommand]
    private void AddFavorite(FavoriteComputerItem? item) => SetFavorite(item, true);

    private void SetFavorite(FavoriteComputerItem? item, bool favorite)
    {
        if (item is null) return;

        try
        {
            var settings = _settings.Load();
            if (favorite)
            {
                if (!settings.FavoriteComputers.Contains(item.Id)) settings.FavoriteComputers.Add(item.Id);
            }
            else
            {
                settings.FavoriteComputers.Remove(item.Id);
            }

            _settings.Save(settings);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось сохранить избранное: {ex.Message}");
            return;
        }

        Reload();
    }

    private void OnNavigated(object? sender, NavigationEventArgs e) =>
        _active = string.Equals(e.PageId, PageIds.Favorites, StringComparison.OrdinalIgnoreCase);

    // События индекса идут пачками; страница перечитывает списки не чаще раза в 250 мс и только пока открыта.
    private void OnIndexChanged(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _changePending, 1) == 1) return;

        _ = Task.Delay(ChangeInterval).ContinueWith(
            _ => UiThread.Post(() =>
            {
                Interlocked.Exchange(ref _changePending, 0);
                if (!_disposed && _active) Reload();
            }),
            TaskScheduler.Default);
    }

    /// <summary>Перечитывает настройки и индекс. Строки с неизменившимся компьютером остаются теми же объектами.</summary>
    internal void Reload()
    {
        AppSettings settings;
        try
        {
            settings = _settings.Load();
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось прочитать избранное: {ex.Message}");
            return;
        }

        var favoriteIds = settings.FavoriteComputers.ToHashSet();
        var missing = 0;

        Sync(Favorites, settings.FavoriteComputers, favoriteIds, ref missing);

        // Недавние: то, что уже в избранном, не дублируется.
        var recentIds = settings.RecentComputers.Where(id => !favoriteIds.Contains(id)).ToList();
        Sync(Recent, recentIds, favoriteIds, ref missing);

        MissingCount = missing;
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Sync(
        ObservableCollection<FavoriteComputerItem> target,
        IReadOnlyList<Guid> ids,
        HashSet<Guid> favoriteIds,
        ref int missing)
    {
        var existing = target.ToDictionary(i => i.Id);
        var wanted = new List<FavoriteComputerItem>(ids.Count);

        foreach (var id in ids)
        {
            var computer = _index.Find(id);
            if (computer is null)
            {
                missing++;
                continue;
            }

            wanted.Add(existing.TryGetValue(id, out var item) && item.Computer == computer
                ? item
                : new FavoriteComputerItem(computer, favoriteIds.Contains(id), new ActionPresenter<ComputerTarget>(_runner, _notifications)));
        }

        if (target.SequenceEqual(wanted)) return;

        target.Clear();
        foreach (var item in wanted) target.Add(item);
    }

    private async Task CheckOnlineAsync(bool force)
    {
        var computers = Favorites.Concat(Recent).Select(i => i.Computer).ToList();
        if (computers.Count == 0) return;

        _onlineCts?.Cancel();
        var cts = new CancellationTokenSource();
        _onlineCts = cts;
        IsChecking = true;
        try
        {
            await _online.RefreshAsync(computers, force, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Страницу закрыли или запустили новую проверку.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) _notifications.Error($"Не удалось проверить доступность: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_onlineCts, cts)) IsChecking = false;
        }

        // Результат уже в индексе; перечитываем, не дожидаясь события.
        if (!cts.IsCancellationRequested && !_disposed) Reload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _index.Changed -= OnIndexChanged;
        _navigation.Navigated -= OnNavigated;
        _onlineCts?.Cancel();
    }
}
