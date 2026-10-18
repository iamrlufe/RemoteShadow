using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Pages;

/// <summary>
/// Страница «Компьютеры»: список из <see cref="IComputerIndex"/>; кнопки для выбранного компьютера объединяют
/// модули Connection и Power (оба регистрируют действия для <see cref="ComputerTarget"/>, их строит один
/// <see cref="ActionPresenter{TTarget}"/>), а кнопки для выбранной сессии — модули Connection (shadow) и Sessions
/// (сообщение, отключение, выход), оба для <see cref="SessionTarget"/>.
/// </summary>
public sealed partial class ComputersPageViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly IComputerIndex _index;
    private readonly ISessionService _sessions;
    private readonly INotificationService _notifications;
    private readonly ActionPresenter<ComputerTarget> _computerPresenter;
    private readonly ActionPresenter<SessionTarget> _sessionPresenter;
    private CancellationTokenSource? _sessionsCts;
    private Guid? _trackedComputerId;
    private int? _pendingSessionId;
    private bool _disposed;

    public ComputersPageViewModel(IComputerIndex index, ISessionService sessions, IActionRunner runner, INotificationService notifications)
    {
        _index = index;
        _sessions = sessions;
        _notifications = notifications;
        _computerPresenter = new ActionPresenter<ComputerTarget>(runner, notifications);
        _sessionPresenter = new ActionPresenter<SessionTarget>(runner, notifications);

        _computerPresenter.PropertyChanged += OnComputerPresenterPropertyChanged;
        _sessionPresenter.PropertyChanged += OnSessionPresenterPropertyChanged;
        _index.Changed += OnIndexChanged;

        RefreshComputers();
    }

    public ObservableCollection<ComputerInfo> Computers { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    public ActionPresenter<ComputerTarget> ComputerPresenter => _computerPresenter;
    public ActionPresenter<SessionTarget> SessionPresenter => _sessionPresenter;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ComputerInfo? _selectedComputer;

    [ObservableProperty]
    private IReadOnlyList<ActionButtonModel> _computerButtons = Array.Empty<ActionButtonModel>();

    [ObservableProperty]
    private bool _sessionsLoading;

    [ObservableProperty]
    private string? _sessionsError;

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    [ObservableProperty]
    private IReadOnlyList<ActionButtonModel> _sessionButtons = Array.Empty<ActionButtonModel>();

    /// <summary>«Без запроса пользователю» для shadow-действий (причина потребуется в диалоге подтверждения).</summary>
    [ObservableProperty]
    private bool _noConsent;

    public bool HasSelection => SelectedComputer is not null;

    public void OnNavigatedTo(object? parameter)
    {
        switch (parameter)
        {
            case SelectComputerRequest request:
                SelectComputer(request.ComputerId, request.SessionId);
                break;

            case FindUserRequest find:
                // Опрос подразделений по IUserLocator.FindAsync ещё не подключён к интерфейсу: явно сообщаем,
                // а не делаем вид, что поиск что-то нашёл.
                _notifications.Info($"Поиск «{find.Text}» по подразделениям ещё не реализован. Используйте поиск Ctrl+K (ищет по уже опрошенным сессиям).");
                break;
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshComputers();

    partial void OnSelectedComputerChanged(ComputerInfo? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        RebuildComputerButtons();

        var id = value?.Id;
        if (id == _trackedComputerId) return; // та же запись, просто обновились поля (например, «в сети»).

        _trackedComputerId = id;
        _ = LoadSessionsAsync(value);
    }

    partial void OnSelectedSessionChanged(SessionInfo? value) => RebuildSessionButtons();

    partial void OnNoConsentChanged(bool value) => RebuildSessionButtons();

    private void SelectComputer(Guid id, int? sessionId)
    {
        var computer = _index.Find(id);
        if (computer is null)
        {
            _notifications.Error("Компьютер не найден в индексе.");
            return;
        }

        _pendingSessionId = sessionId;
        SelectedComputer = computer;

        // Тот же компьютер уже был выбран: сессии не перезагружаются, поэтому выбираем сразу.
        if (sessionId is { } sid) SelectedSession = Sessions.FirstOrDefault(s => s.Id == sid);
    }

    private void RebuildComputerButtons()
    {
        ComputerButtons = SelectedComputer is { } computer
            ? _computerPresenter.BuildButtons(new[] { new ComputerTarget(computer) }, null)
            : Array.Empty<ActionButtonModel>();
    }

    private void RebuildSessionButtons()
    {
        if (SelectedComputer is not { } computer || SelectedSession is not { } session)
        {
            SessionButtons = Array.Empty<ActionButtonModel>();
            return;
        }

        var parameters = NoConsent
            ? new Dictionary<string, string> { [ActionParameters.NoConsent] = "true" }
            : null;
        SessionButtons = _sessionPresenter.BuildButtons(new[] { new SessionTarget(computer, session) }, parameters);
    }

    private async Task LoadSessionsAsync(ComputerInfo? computer)
    {
        _sessionsCts?.Cancel();
        Sessions.Clear();
        SelectedSession = null;
        SessionsError = null;

        if (computer is null)
        {
            SessionsLoading = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _sessionsCts = cts;
        SessionsLoading = true;
        try
        {
            var result = await _sessions.GetSessionsAsync(computer.HostName, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (result.IsOk)
            {
                foreach (var session in result.Sessions) Sessions.Add(session);
                if (_pendingSessionId is { } sid)
                {
                    SelectedSession = Sessions.FirstOrDefault(s => s.Id == sid);
                    _pendingSessionId = null;
                }
            }
            else
            {
                SessionsError = result.Message ?? "Не удалось получить список сессий.";
            }
        }
        catch (OperationCanceledException)
        {
            // Выбрали другой компьютер — отменённая загрузка не ошибка.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) SessionsError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_sessionsCts, cts)) SessionsLoading = false;
        }
    }

    private void OnComputerPresenterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Сброс/выключение компьютера и т. п. не меняют список — только сами кнопки (CanExecute).
        if (e.PropertyName == nameof(ActionPresenter<ComputerTarget>.IsBusy) && !_computerPresenter.IsBusy)
            RebuildComputerButtons();
    }

    private void OnSessionPresenterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Отключение/выход меняют список сессий — перечитываем его после завершения действия.
        if (e.PropertyName == nameof(ActionPresenter<SessionTarget>.IsBusy) && !_sessionPresenter.IsBusy)
            _ = LoadSessionsAsync(SelectedComputer);
    }

    private void OnIndexChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        if (!_disposed) RefreshComputers();
    });

    private void RefreshComputers()
    {
        var text = SearchText.Trim();
        var source = text.Length == 0 ? _index.All : _index.Search(text, 200);

        var selectedId = SelectedComputer?.Id;
        Computers.Clear();
        foreach (var computer in source.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Computers.Add(computer);

        SelectedComputer = selectedId is { } id ? Computers.FirstOrDefault(c => c.Id == id) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _index.Changed -= OnIndexChanged;
        _computerPresenter.PropertyChanged -= OnComputerPresenterPropertyChanged;
        _sessionPresenter.PropertyChanged -= OnSessionPresenterPropertyChanged;
        _sessionsCts?.Cancel();
    }
}
