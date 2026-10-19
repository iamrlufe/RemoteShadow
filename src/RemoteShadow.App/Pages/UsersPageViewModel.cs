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
/// Страница «Пользователи AD»: поиск по <see cref="IUserDirectory"/> и действия модуля Users
/// (сброс пароля, блокировка, включение/отключение, группы) для выбранного пользователя.
/// </summary>
public sealed partial class UsersPageViewModel : ObservableObject, INavigationAware, IRefreshablePage, IDisposable
{
    private const int SearchDelayMs = 200;
    private const int MaxResults = 50;

    private readonly IUserDirectory _users;
    private readonly INotificationService _notifications;
    private readonly INavigationService? _navigation;
    private readonly ActionPresenter<UserTarget> _presenter;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _groupsCts;
    private bool _rebuildingResults;
    private bool _restoringSelection;
    private bool _disposed;

    public UsersPageViewModel(IUserDirectory users, IActionRunner runner, INotificationService notifications, INavigationService? navigation = null)
    {
        _users = users;
        _notifications = notifications;
        _navigation = navigation;
        _presenter = new ActionPresenter<UserTarget>(runner, notifications);
        _presenter.PropertyChanged += OnPresenterPropertyChanged;
    }

    public ActionPresenter<UserTarget> Presenter => _presenter;
    public ObservableCollection<UserInfo> Results { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string? _searchError;

    [ObservableProperty]
    private UserInfo? _selectedUser;

    [ObservableProperty]
    private IReadOnlyList<ActionButtonModel> _buttons = Array.Empty<ActionButtonModel>();

    [ObservableProperty]
    private IReadOnlyList<GroupInfo> _groups = Array.Empty<GroupInfo>();

    [ObservableProperty]
    private bool _groupsLoading;

    /// <summary>Поиск выполнен и ничего не нашёл — показывается «Никто не найден».</summary>
    [ObservableProperty]
    private bool _hasNoResults;

    public bool HasSelection => SelectedUser is not null;

    /// <summary>Строка под ФИО: логин, должность, отдел — без пустых частей.</summary>
    public string? SelectedUserSubtitle => SelectedUser is { } user
        ? string.Join(" · ", new[] { user.SamAccountName, user.Title, user.Department }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    /// <summary>F5 — повторить поиск (и перечитать выбранного пользователя).</summary>
    public void RefreshPage(bool full)
    {
        _ = SearchAsync(SearchText);
        if (SelectedUser is { } user) _ = RefreshSelectedUserAsync(user.SamAccountName);
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is SelectUserRequest request) _ = SelectUserAsync(request.SamAccountName);
    }

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    partial void OnSelectedUserChanged(UserInfo? value)
    {
        // Список перестраивается (новый поиск): ListBox временно сбрасывает выбор, это не смена пользователя.
        if (_rebuildingResults) return;

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedUserSubtitle));
        RebuildButtons();

        // Тот же пользователь вернулся в выбор после нового поиска — его группы уже показаны.
        if (_restoringSelection) return;

        _ = LoadGroupsAsync(value);
    }

    private void RebuildButtons() =>
        Buttons = SelectedUser is { } user
            ? _presenter.BuildButtons(new[] { new UserTarget(user) }, null)
            : Array.Empty<ActionButtonModel>();

    private async Task SearchAsync(string text)
    {
        _searchCts?.Cancel();

        var trimmed = text.Trim();
        if (trimmed.Length < 2)
        {
            var cancelled = new CancellationTokenSource();
            _searchCts = cancelled;
            ReplaceResults(Array.Empty<UserInfo>());
            SearchError = null;
            IsSearching = false;
            HasNoResults = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        SearchError = null;
        HasNoResults = false;
        try
        {
            await Task.Delay(SearchDelayMs, cts.Token);
            var found = await Task.Run(() => _users.SearchAsync(trimmed, MaxResults, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            ReplaceResults(found);
            HasNoResults = Results.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // Новый ввод отменил предыдущий поиск — не ошибка.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) SearchError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    /// <summary>
    /// Заменяет результаты поиска, сохраняя выбранного пользователя, если он есть в новом списке (иначе выбор
    /// снимается). Без этого ListBox при очистке списка сбрасывал выбор — например, сразу после перехода из Ctrl+K.
    /// </summary>
    private void ReplaceResults(IReadOnlyList<UserInfo> found)
    {
        var previous = SelectedUser;
        var keep = previous?.SamAccountName;

        _rebuildingResults = true;
        try
        {
            Results.Clear();
            foreach (var user in found) Results.Add(user);
        }
        finally
        {
            _rebuildingResults = false;
        }

        var match = keep is null ? null : Results.FirstOrDefault(u => string.Equals(u.SamAccountName, keep, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            // Пользователя нет в новых результатах: выбор снимается. Если ListBox уже обнулил его во время
            // перестройки (тогда изменение было подавлено), карточку нужно обновить вручную.
            if (SelectedUser is not null) SelectedUser = null;
            else if (previous is not null) OnSelectedUserChanged(null);
            return;
        }

        if (ReferenceEquals(SelectedUser, match)) return;

        _restoringSelection = true;
        try
        {
            SelectedUser = match;
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private async Task LoadGroupsAsync(UserInfo? user)
    {
        // Быстро выбрали другого пользователя: ответ по прежнему не должен затереть группы нового.
        _groupsCts?.Cancel();
        Groups = Array.Empty<GroupInfo>();
        if (user is null)
        {
            GroupsLoading = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _groupsCts = cts;
        GroupsLoading = true;
        try
        {
            var groups = await _users.GetGroupsAsync(user.DistinguishedName, cts.Token);
            if (!cts.IsCancellationRequested) Groups = groups;
        }
        catch (OperationCanceledException)
        {
            // Выбрали другого пользователя.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) _notifications.Error($"Не удалось получить группы пользователя: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_groupsCts, cts)) GroupsLoading = false;
        }
    }

    // ----- двойной щелчок, меню, копирование ----------------------------------------------------------------------

    /// <summary>Двойной щелчок или Enter по пользователю: найти, на каком компьютере он работает.</summary>
    [RelayCommand]
    private void FindWhereLoggedOn()
    {
        if (SelectedUser is not { } user) return;

        if (_navigation is null)
        {
            _notifications.Error("Поиск пользователя на компьютерах недоступен.");
            return;
        }

        _navigation.NavigateTo(PageIds.Computers, new FindUserRequest(user.SamAccountName));
    }

    [RelayCommand]
    private void CopyLogin() => ClipboardText.Copy(SelectedUser?.SamAccountName, _notifications);

    /// <summary>Меню строки пользователя: поиск по компьютерам, действия модуля Users и копирование.</summary>
    public IReadOnlyList<ActionButtonModel>? BuildUserMenu()
    {
        if (SelectedUser is not { } user) return null;

        var items = new List<ActionButtonModel>();
        if (_navigation is not null)
        {
            items.Add(ActionButtons.Local("local.find-user", "Где работает?", ActionButtons.NavigateGroup, "search",
                FindWhereLoggedOn, "Найти компьютеры, на которых пользователь сейчас в сеансе"));
        }

        items.AddRange(Buttons);
        items.Add(ActionButtons.Local("local.copy-login", "Копировать логин", ActionButtons.CopyGroup, "copy",
            () => ClipboardText.Copy(user.SamAccountName, _notifications)));
        if (!string.IsNullOrWhiteSpace(user.UserPrincipalName))
        {
            items.Add(ActionButtons.Local("local.copy-upn", "Копировать UPN", ActionButtons.CopyGroup, null,
                () => ClipboardText.Copy(user.UserPrincipalName, _notifications)));
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            items.Add(ActionButtons.Local("local.copy-name", "Копировать ФИО", ActionButtons.CopyGroup, null,
                () => ClipboardText.Copy(user.DisplayName, _notifications)));
        }

        return items;
    }

    private async Task SelectUserAsync(string samAccountName)
    {
        SearchText = samAccountName;
        try
        {
            var user = await _users.GetBySamAccountNameAsync(samAccountName, CancellationToken.None);
            if (user is null)
            {
                _notifications.Error($"Пользователь «{samAccountName}» не найден.");
                return;
            }

            if (Results.All(u => !string.Equals(u.SamAccountName, user.SamAccountName, StringComparison.OrdinalIgnoreCase)))
                Results.Insert(0, user);

            SelectedUser = Results.FirstOrDefault(u => string.Equals(u.SamAccountName, user.SamAccountName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось найти пользователя: {ex.Message}");
        }
    }

    // Пароль сброшен, учётная запись заблокирована/включена и т. п. — карточка и кнопки должны отразить новое состояние.
    private void OnPresenterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActionPresenter<UserTarget>.IsBusy) || _presenter.IsBusy) return;
        if (SelectedUser is not { } user) return;

        _ = RefreshSelectedUserAsync(user.SamAccountName);
    }

    private async Task RefreshSelectedUserAsync(string samAccountName)
    {
        try
        {
            var refreshed = await _users.GetBySamAccountNameAsync(samAccountName, CancellationToken.None);
            if (refreshed is null) return;

            var index = Results.ToList().FindIndex(u => string.Equals(u.SamAccountName, samAccountName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                // Замена строки сбрасывает выбор в ListBox — это не смена пользователя.
                _rebuildingResults = true;
                try
                {
                    Results[index] = refreshed;
                }
                finally
                {
                    _rebuildingResults = false;
                }
            }

            // Добавление в группу не меняет саму учётную запись: без события выбора группы перечитываются явно.
            if (Equals(SelectedUser, refreshed)) _ = LoadGroupsAsync(refreshed);
            else SelectedUser = refreshed;
        }
        catch (Exception)
        {
            // Карточка просто не обновится — пользователь может нажать «Обновить» поиск заново.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _presenter.PropertyChanged -= OnPresenterPropertyChanged;
        _searchCts?.Cancel();
        _groupsCts?.Cancel();
    }
}
