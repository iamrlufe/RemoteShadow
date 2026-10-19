using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Pages;

/// <summary>
/// Клавиатура, двойной щелчок, контекстные меню, копирование и «Найти пользователя на компьютерах» страницы «Компьютеры».
/// </summary>
public sealed partial class ComputersPageViewModel
{
    /// <summary>Главное действие для компьютера (двойной щелчок, Enter в списке).</summary>
    internal static readonly string[] ComputerMainActionIds = ["connection.rdp"];

    /// <summary>Главное действие для сессии: shadow-просмотр, а если он недоступен — управление.</summary>
    internal static readonly string[] SessionMainActionIds = ["connection.shadow-view", "connection.shadow-control"];

    /// <summary>Сколько компьютеров «Найти пользователя» опрашивает без подтверждения.</summary>
    public const int FindUserConfirmThreshold = 50;

    private CancellationTokenSource? _findCts;

    // ----- F5 / Ctrl+F5 -------------------------------------------------------------------------------------------

    /// <summary>F5 — перепроверить статусы области и сессии выбранного компьютера; Ctrl+F5 — заново загрузить список из AD.</summary>
    public void RefreshPage(bool full)
    {
        if (full)
        {
            if (RefreshListCommand.CanExecute(null)) RefreshListCommand.Execute(null);
            return;
        }

        if (RefreshVisibleAvailabilityCommand.CanExecute(null)) RefreshVisibleAvailabilityCommand.Execute(null);
        if (SelectedComputer is { } computer) _ = LoadSessionsAsync(computer);
    }

    // ----- карточка ------------------------------------------------------------------------------------------------

    /// <summary>Строка под именем компьютера: ОС, полное имя и IP — без пустых частей и лишних разделителей.</summary>
    public string? SelectedComputerSubtitle
    {
        get
        {
            if (SelectedComputer is not { } computer) return null;

            var parts = new List<string?> { computer.OperatingSystem };
            if (!string.Equals(computer.HostName, computer.Name, StringComparison.OrdinalIgnoreCase)) parts.Add(computer.HostName);
            parts.Add(computer.IpAddresses?.FirstOrDefault());
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    partial void OnSelectedComputerChanged(ComputerInfo? oldValue, ComputerInfo? newValue) =>
        OnPropertyChanged(nameof(SelectedComputerSubtitle));

    // ----- главное действие строки ---------------------------------------------------------------------------------

    /// <summary>Двойной щелчок или Enter по компьютеру: подключение по RDP.</summary>
    [RelayCommand]
    private void RunComputerMainAction()
    {
        if (SelectedComputer is null) return;
        ActionButtons.ExecuteFirst(ComputerButtons, _notifications, ComputerMainActionIds);
    }

    /// <summary>Двойной щелчок или Enter по сессии: shadow-просмотр (с учётом «Без запроса пользователю»).</summary>
    [RelayCommand]
    private void RunSessionMainAction()
    {
        if (SelectedSession is null) return;
        ActionButtons.ExecuteFirst(SessionButtons, _notifications, SessionMainActionIds);
    }

    // ----- копирование ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private void CopyComputerName() => ClipboardText.Copy(SelectedComputer?.Name, _notifications);

    [RelayCommand]
    private void CopySessionUser() => ClipboardText.Copy(SessionUserName(SelectedSession), _notifications);

    /// <summary>Логин из сессии без домена («CORP\ivanov» → «ivanov»), чтобы вставить в поиск или заявку.</summary>
    internal static string? SessionUserName(SessionInfo? session) =>
        session is { HasUser: true } ? session.UserName : null;

    // ----- контекстные меню ----------------------------------------------------------------------------------------

    /// <summary>Меню строки компьютера: действия модулей, избранное, диагностика и копирование.</summary>
    public IReadOnlyList<ActionButtonModel>? BuildComputerMenu()
    {
        if (SelectedComputer is not { } computer) return null;

        var items = new List<ActionButtonModel>(ComputerButtons);

        items.Add(ActionButtons.Local("local.favorite", IsSelectedFavorite ? "Убрать из избранного" : "Добавить в избранное",
            ActionButtons.NavigateGroup, "star", () => ToggleFavorite(computer)));
        if (_navigation is not null)
        {
            items.Add(ActionButtons.Local("local.diagnostics", "Диагностика shadow", ActionButtons.NavigateGroup, "stethoscope",
                () => _navigation.NavigateTo(PageIds.Diagnostics, new DiagnoseComputerRequest(computer.Id))));
        }

        items.Add(ActionButtons.Local("local.copy-name", "Копировать имя", ActionButtons.CopyGroup, "copy",
            () => ClipboardText.Copy(computer.Name, _notifications)));
        if (!string.IsNullOrWhiteSpace(computer.HostName) && !string.Equals(computer.HostName, computer.Name, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(ActionButtons.Local("local.copy-fqdn", "Копировать полное имя (FQDN)", ActionButtons.CopyGroup, null,
                () => ClipboardText.Copy(computer.HostName, _notifications)));
        }

        if (computer.IpAddresses?.FirstOrDefault() is { Length: > 0 } ip)
            items.Add(ActionButtons.Local("local.copy-ip", "Копировать IP-адрес", ActionButtons.CopyGroup, null, () => ClipboardText.Copy(ip, _notifications)));

        return items;
    }

    /// <summary>Меню строки сессии: shadow, сообщение, отключение, выход и копирование логина.</summary>
    public IReadOnlyList<ActionButtonModel>? BuildSessionMenu()
    {
        if (SelectedSession is not { } session) return null;

        var items = new List<ActionButtonModel>(SessionButtons);
        if (SessionUserName(session) is { Length: > 0 } login)
        {
            items.Add(ActionButtons.Local("local.copy-login", "Копировать логин", ActionButtons.CopyGroup, "copy",
                () => ClipboardText.Copy(login, _notifications)));
        }

        return items;
    }

    // ----- «Найти пользователя на компьютерах» ---------------------------------------------------------------------

    /// <summary>Где найден пользователь (живые сессии и записи из кэша), по мере ответа компьютеров.</summary>
    public ObservableCollection<UserLocation> FoundLocations { get; } = new();

    /// <summary>Что ищем; null — панель поиска пользователя скрыта.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindPanel))]
    private string? _findQuery;

    [ObservableProperty]
    private bool _isFinding;

    [ObservableProperty]
    private string? _findStatus;

    [ObservableProperty]
    private UserLocation? _selectedLocation;

    public bool HasFindPanel => FindQuery is not null;

    /// <summary>Выполняющийся поиск пользователя (для тестов).</summary>
    public Task FindUserTask { get; private set; } = Task.CompletedTask;

    partial void OnSelectedLocationChanged(UserLocation? value)
    {
        if (value is not null) SelectComputer(value.Computer.Id, value.Session.Id);
    }

    /// <summary>
    /// Опрашивает компьютеры домена и показывает, где работает пользователь (<see cref="IUserLocator.FindAsync"/>).
    /// Сначала приходят совпадения из кэша сессий, затем живые результаты. Большой опрос требует подтверждения:
    /// массовые подключения к компьютерам замечают антивирусы и EDR.
    /// </summary>
    internal async Task FindUserAsync(string text)
    {
        var query = text.Trim();
        if (query.Length == 0) return;

        if (_locator is null)
        {
            _notifications.Error("Поиск пользователя на компьютерах недоступен.");
            return;
        }

        var candidates = _index.All.Count(c => c.Enabled);
        if (candidates > FindUserConfirmThreshold)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
                "Найти пользователя на компьютерах",
                $"Будут опрошены компьютеры домена ({candidates}), чтобы найти сессии «{query}». Сначала проверяется порт 445, "
                + "затем у доступных компьютеров запрашивается список сессий. Массовый опрос может занять время и привлечь внимание антивируса или EDR.",
                new[] { query }));
            if (!confirmed) return;
        }

        _findCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findCts = cts;

        FoundLocations.Clear();
        FindQuery = query;
        FindStatus = $"Опрос компьютеров ({candidates})…";
        IsFinding = true;
        try
        {
            await foreach (var location in _locator.FindAsync(query, scopeOuDn: null, includeNestedOu: true, cts.Token))
            {
                if (cts.IsCancellationRequested) break;

                // Сеанс может прийти дважды: из кэша, потом живым — заменяем запись.
                var existing = FoundLocations.Select((l, i) => (l, i))
                    .FirstOrDefault(x => x.l.Computer.Id == location.Computer.Id && x.l.Session.Id == location.Session.Id);
                if (existing.l is not null) FoundLocations[existing.i] = location;
                else FoundLocations.Add(location);

                FindStatus = $"Опрос компьютеров… найдено сессий: {FoundLocations.Count}";
            }

            if (!cts.IsCancellationRequested)
            {
                FindStatus = FoundLocations.Count == 0
                    ? $"Сессии пользователя «{query}» не найдены."
                    : $"Готово. Найдено сессий: {FoundLocations.Count}. Выберите строку, чтобы открыть компьютер.";
            }
        }
        catch (OperationCanceledException)
        {
            FindStatus = FoundLocations.Count == 0 ? "Поиск остановлен." : $"Поиск остановлен. Найдено сессий: {FoundLocations.Count}.";
        }
        catch (Exception ex)
        {
            FindStatus = $"Ошибка поиска: {ex.Message}";
            _notifications.Error($"Не удалось найти пользователя: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_findCts, cts)) IsFinding = false;
        }
    }

    [RelayCommand]
    private void CancelFind() => _findCts?.Cancel();

    /// <summary>Закрывает панель «Найти пользователя» (и останавливает опрос).</summary>
    [RelayCommand]
    private void CloseFind()
    {
        _findCts?.Cancel();
        FindQuery = null;
        FindStatus = null;
        SelectedLocation = null;
        FoundLocations.Clear();
    }
}
