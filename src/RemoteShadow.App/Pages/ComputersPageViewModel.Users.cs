using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Pages;

/// <summary>Вкладка «Люди»: кто за каким компьютером работает, с подключением сразу к сеансу пользователя.</summary>
public sealed partial class ComputersPageViewModel
{
    private readonly List<UserLocation> _allUserSessions = new();
    private CancellationTokenSource? _usersCts;
    private bool _rebuildingUserSessions;
    private int? _connectWhenLoadedSessionId;

    /// <summary>Сессии пользователей с учётом фильтра, по логину.</summary>
    public ObservableCollection<UserLocation> UserSessions { get; } = new();

    [ObservableProperty]
    private string _usersFilter = string.Empty;

    [ObservableProperty]
    private bool _isListingUsers;

    [ObservableProperty]
    private string? _usersStatus;

    [ObservableProperty]
    private UserLocation? _selectedUserSession;

    /// <summary>Выполняющийся опрос пользователей (для тестов).</summary>
    public Task ScanUsersTask { get; private set; } = Task.CompletedTask;

    partial void OnUsersFilterChanged(string value) => ApplyUsersFilter();

    partial void OnSelectedUserSessionChanged(UserLocation? value)
    {
        if (value is not null && !_rebuildingUserSessions) SelectComputer(value.Computer.Id, value.Session.Id);
    }

    /// <summary>Показывает сессии из кэша (без сети): вызывается при открытии вкладки.</summary>
    public void LoadUserSessionsFromCache()
    {
        if (IsListingUsers || _locator is null) return;

        _allUserSessions.Clear();
        _allUserSessions.AddRange(_locator.ListCached());
        ApplyUsersFilter();
        UsersStatus = _allUserSessions.Count == 0
            ? "Данных о сессиях пока нет. Нажмите «Опросить компьютеры»."
            : $"Сессий: {_allUserSessions.Count} (данные из кэша). Нажмите «Опросить компьютеры», чтобы обновить.";
    }

    private void ApplyUsersFilter()
    {
        var text = UsersFilter.Trim();
        var keep = SelectedUserSession;
        var rows = _allUserSessions
            .Where(l => text.Length == 0
                        || l.Session.AccountName.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || l.Computer.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Session.AccountName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Computer.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Session.Id)
            .ToList();

        _rebuildingUserSessions = true;
        try
        {
            UserSessions.Clear();
            foreach (var row in rows) UserSessions.Add(row);
            SelectedUserSession = keep is null
                ? null
                : UserSessions.FirstOrDefault(l => l.Computer.Id == keep.Computer.Id && l.Session.Id == keep.Session.Id);
        }
        finally
        {
            _rebuildingUserSessions = false;
        }
    }

    /// <summary>Опрашивает компьютеры домена и собирает сессии всех пользователей. Большой опрос требует подтверждения (EDR).</summary>
    [RelayCommand]
    private Task ScanUsers() => ScanUsersTask = ScanUsersAsync();

    internal async Task ScanUsersAsync()
    {
        if (_locator is null)
        {
            _notifications.Error("Опрос пользователей недоступен.");
            return;
        }

        var candidates = _index.All.Count(c => c.Enabled);
        if (candidates > FindUserConfirmThreshold)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
                "Опросить компьютеры",
                $"Будут опрошены компьютеры домена ({candidates}), чтобы собрать список пользователей. Сначала проверяется порт 445, "
                + "затем у доступных компьютеров запрашивается список сессий. Массовый опрос может занять время и привлечь внимание антивируса или EDR.",
                new[] { "все компьютеры" }));
            if (!confirmed) return;
        }

        _usersCts?.Cancel();
        var cts = new CancellationTokenSource();
        _usersCts = cts;
        IsListingUsers = true;
        UsersStatus = $"Опрос компьютеров ({candidates})…";
        try
        {
            await foreach (var location in _locator.ListAllAsync(cts.Token))
            {
                if (cts.IsCancellationRequested) break;

                var i = _allUserSessions.FindIndex(l => l.Computer.Id == location.Computer.Id && l.Session.Id == location.Session.Id);
                if (i >= 0) _allUserSessions[i] = location;
                else _allUserSessions.Add(location);

                ApplyUsersFilter();
                UsersStatus = $"Опрос компьютеров… сессий: {_allUserSessions.Count}";
            }
        }
        catch (OperationCanceledException)
        {
            // Остановлено пользователем.
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось опросить компьютеры: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_usersCts, cts))
            {
                IsListingUsers = false;

                // Кэш обновлён опросом: убираем сеансы, которых на опрошенных компьютерах уже нет.
                _allUserSessions.Clear();
                _allUserSessions.AddRange(_locator.ListCached());
                ApplyUsersFilter();
                UsersStatus = cts.IsCancellationRequested
                    ? $"Опрос остановлен. Сессий: {_allUserSessions.Count}."
                    : $"Готово. Сессий: {_allUserSessions.Count}. Двойной щелчок по строке — подключение к сеансу.";
            }
        }
    }

    [RelayCommand]
    private void CancelUsersScan() => _usersCts?.Cancel();

    /// <summary>Двойной щелчок или Enter по сеансу пользователя: открыть компьютер и сразу подключиться (shadow-просмотр).</summary>
    internal void ConnectToUserSession(UserLocation location)
    {
        _connectWhenLoadedSessionId = null;
        SelectComputer(location.Computer.Id, location.Session.Id);

        if (SelectedComputer?.Id == location.Computer.Id && SelectedSession?.Id == location.Session.Id) RunSessionMainAction();
        else _connectWhenLoadedSessionId = location.Session.Id;
    }

    private void ConnectIfRequested(int loadedSessionId)
    {
        if (_connectWhenLoadedSessionId != loadedSessionId) return;

        _connectWhenLoadedSessionId = null;
        if (SelectedSession?.Id == loadedSessionId) RunSessionMainAction();
    }
}
