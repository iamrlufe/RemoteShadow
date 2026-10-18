using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
public sealed partial class UsersPageViewModel : ObservableObject, INavigationAware, IDisposable
{
    private const int SearchDelayMs = 200;
    private const int MaxResults = 50;

    private readonly IUserDirectory _users;
    private readonly INotificationService _notifications;
    private readonly ActionPresenter<UserTarget> _presenter;
    private CancellationTokenSource? _searchCts;
    private bool _disposed;

    public UsersPageViewModel(IUserDirectory users, IActionRunner runner, INotificationService notifications)
    {
        _users = users;
        _notifications = notifications;
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

    public bool HasSelection => SelectedUser is not null;

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is SelectUserRequest request) _ = SelectUserAsync(request.SamAccountName);
    }

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    partial void OnSelectedUserChanged(UserInfo? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        RebuildButtons();
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
            Results.Clear();
            SearchError = null;
            IsSearching = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        SearchError = null;
        try
        {
            await Task.Delay(SearchDelayMs, cts.Token);
            var found = await Task.Run(() => _users.SearchAsync(trimmed, MaxResults, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            Results.Clear();
            foreach (var user in found) Results.Add(user);
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

    private async Task LoadGroupsAsync(UserInfo? user)
    {
        Groups = Array.Empty<GroupInfo>();
        if (user is null) return;

        GroupsLoading = true;
        try
        {
            Groups = await _users.GetGroupsAsync(user.DistinguishedName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось получить группы пользователя: {ex.Message}");
        }
        finally
        {
            GroupsLoading = false;
        }
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
            if (index >= 0) Results[index] = refreshed;
            SelectedUser = refreshed;
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
    }
}
