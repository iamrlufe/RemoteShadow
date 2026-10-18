using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.App.Shell.Converters;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell.Search;

/// <summary>
/// Панель поиска Ctrl+K. Задержка ввода 150 мс; секции: «Где сейчас» (кэш сессий), «Компьютеры» (индекс в памяти),
/// «Пользователи» (каталог, с задержкой 250 мс и отменой предыдущего запроса) и последний пункт «Найти пользователя…».
/// Всё, что связано с окном (фокус, всплывающая панель), делает <c>MainWindow</c>.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    public static readonly TimeSpan LocalDelay = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan UsersDelay = TimeSpan.FromMilliseconds(250);

    public const int MaxLocations = 5;
    public const int MaxComputers = 8;
    public const int MaxUsers = 6;

    /// <summary>Короче этого запрос в каталог не отправляется: «а» вернул бы случайных людей.</summary>
    public const int MinUserQueryLength = 2;

    private enum UsersState
    {
        None,
        Loading,
        Done,
    }

    private readonly IComputerIndex _index;
    private readonly IUserLocator _locator;
    private readonly IUserDirectory _users;
    private readonly INavigationService _navigation;

    // ФИО, виденные в результатах каталога: по ним «Где сейчас» показывает имя, а не только логин.
    private readonly Dictionary<string, string> _knownNames = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private string _query = string.Empty;
    private IReadOnlyList<UserLocation> _locations = Array.Empty<UserLocation>();
    private IReadOnlyList<ComputerInfo> _computers = Array.Empty<ComputerInfo>();
    private IReadOnlyList<UserInfo> _foundUsers = Array.Empty<UserInfo>();
    private UsersState _usersState;
    private bool _inputFocused;

    public SearchViewModel(
        IComputerIndex index,
        IUserLocator locator,
        IUserDirectory users,
        INavigationService navigation)
    {
        _index = index;
        _locator = locator;
        _users = users;
        _navigation = navigation;
    }

    /// <summary>Текст в поле поиска.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Открыта ли всплывающая панель.</summary>
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private IReadOnlyList<SearchItem> _items = Array.Empty<SearchItem>();

    [ObservableProperty]
    private SearchItem? _selectedItem;

    /// <summary>Пользователь выбрал результат (или закрыл панель выбором): окну пора убрать фокус из поля.</summary>
    public event EventHandler? ResultChosen;

    /// <summary>Окно сообщает, что поле поиска получило или потеряло фокус. Панель открывается только при фокусе.</summary>
    public void SetInputFocused(bool focused)
    {
        _inputFocused = focused;
        if (focused)
        {
            if (HasQuery) Schedule(immediate: true);
        }
        else
        {
            Close();
        }
    }

    /// <summary>Закрывает панель, не очищая текст.</summary>
    public void Close()
    {
        CancelPending();
        IsOpen = false;
    }

    /// <summary>Esc: сначала закрыть панель, затем очистить текст. false — делать больше нечего.</summary>
    public bool Dismiss()
    {
        if (IsOpen)
        {
            Close();
            return true;
        }

        if (HasQuery)
        {
            Text = string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>Стрелки вверх и вниз: к следующей выбираемой строке (по кругу).</summary>
    public void MoveSelection(int step)
    {
        var selectable = Items.Where(i => i.IsSelectable).ToList();
        if (selectable.Count == 0) return;

        var index = SelectedItem is null ? -1 : selectable.IndexOf(SelectedItem);
        if (index < 0) index = step > 0 ? -1 : 0;

        SelectedItem = selectable[(index + step + selectable.Count) % selectable.Count];
    }

    /// <summary>Enter: открыть выбранную строку. Если ввод ещё не успел дать результатов, они ищутся сразу, без задержки.</summary>
    [RelayCommand]
    private void CommitSelected()
    {
        if (SelectedItem is null && HasQuery)
        {
            CancelPending();
            ApplyLocalResults(Text.Trim());
        }

        if (SelectedItem is { IsSelectable: true } item) Choose(item);
    }

    /// <summary>Щелчок по строке.</summary>
    [RelayCommand]
    private void Choose(SearchItem? item)
    {
        if (item is not { IsSelectable: true }) return;

        switch (item.Kind)
        {
            case SearchItemKind.Location:
            case SearchItemKind.Computer:
                if (item.ComputerId is { } computerId)
                    _navigation.NavigateTo(PageIds.Computers, new SelectComputerRequest(computerId, item.SessionId));
                break;

            case SearchItemKind.User:
                if (item.SamAccountName is { } sam)
                    _navigation.NavigateTo(PageIds.Users, new SelectUserRequest(sam));
                break;

            case SearchItemKind.FindUser:
                if (item.Query is { } query)
                    _navigation.NavigateTo(PageIds.Computers, new FindUserRequest(query));
                break;
        }

        // Текст очищается: панель закрывается, а следующий Ctrl+K начинает поиск заново.
        Text = string.Empty;
        ResultChosen?.Invoke(this, EventArgs.Empty);
    }

    private bool HasQuery => !string.IsNullOrWhiteSpace(Text);

    partial void OnTextChanged(string value)
    {
        if (!_inputFocused && !string.IsNullOrWhiteSpace(value))
        {
            // Текст поменяли не вводом (например, вставкой в код): панель без фокуса не открываем.
            return;
        }

        Schedule(immediate: false);
    }

    private void Schedule(bool immediate)
    {
        CancelPending();

        var query = Text.Trim();
        if (query.Length == 0)
        {
            ResetResults();
            IsOpen = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = SearchAsync(query, immediate, cts.Token);
    }

    private async Task SearchAsync(string query, bool immediate, CancellationToken ct)
    {
        try
        {
            if (!immediate) await Task.Delay(LocalDelay, ct);

            ApplyLocalResults(query);
            IsOpen = true;

            if (_usersState != UsersState.Loading) return;

            // Каталог опрашивается позже, чтобы не нагружать его на каждую букву: всего 250 мс после последнего ввода.
            var rest = UsersDelay - LocalDelay;
            if (!immediate && rest > TimeSpan.Zero) await Task.Delay(rest, ct);

            // Запрос уходит из потока интерфейса: служба каталога может блокироваться до первого await.
            var found = await Task.Run(() => _users.SearchAsync(query, MaxUsers, ct), ct);
            ct.ThrowIfCancellationRequested();

            _foundUsers = found;
            foreach (var user in found) _knownNames[user.SamAccountName] = user.ShownName;
            _usersState = UsersState.Done;
            _locations = SafeFindCached(query);
            Rebuild();
        }
        catch (OperationCanceledException)
        {
            // Пришёл новый ввод или панель закрыли.
        }
        catch (Exception)
        {
            // Ошибки каталога (нет входа, сеть) в поиске не показываем: секция «Пользователи» просто пропадает.
            if (ct.IsCancellationRequested) return;

            _foundUsers = Array.Empty<UserInfo>();
            _usersState = UsersState.None;
            Rebuild();
        }
    }

    // Результаты, не требующие обращения к каталогу: кэш сессий и индекс компьютеров (оба в памяти).
    private void ApplyLocalResults(string query)
    {
        _query = query;
        _locations = SafeFindCached(query);
        _computers = SafeSearchComputers(query);
        _foundUsers = Array.Empty<UserInfo>();
        _usersState = query.Length >= MinUserQueryLength ? UsersState.Loading : UsersState.None;
        Rebuild();
    }

    private void Rebuild()
    {
        var list = new List<SearchItem>();

        if (_locations.Count > 0)
        {
            list.Add(SearchItem.CreateHeader("Где сейчас"));
            foreach (var location in _locations.Take(MaxLocations)) list.Add(CreateLocationItem(location));
        }

        if (_computers.Count > 0)
        {
            list.Add(SearchItem.CreateHeader("Компьютеры"));
            foreach (var computer in _computers.Take(MaxComputers)) list.Add(CreateComputerItem(computer));
        }

        if (_usersState == UsersState.Loading)
        {
            list.Add(SearchItem.CreateHeader("Пользователи"));
            list.Add(SearchItem.CreateMessage("users", "Поиск…"));
        }
        else if (_foundUsers.Count > 0)
        {
            list.Add(SearchItem.CreateHeader("Пользователи"));
            foreach (var user in _foundUsers.Take(MaxUsers)) list.Add(CreateUserItem(user));
        }

        list.Add(SearchItem.CreateFindUser(_query));

        var previousKey = SelectedItem?.Key;
        Items = list;
        SelectedItem = list.FirstOrDefault(i => i.IsSelectable && i.Key == previousKey)
                       ?? list.FirstOrDefault(i => i.IsSelectable);
    }

    private SearchItem CreateLocationItem(UserLocation location)
    {
        var login = location.Session.UserName ?? string.Empty;
        var who = _knownNames.TryGetValue(login, out var name) ? name : login;
        var title = $"{who} · {location.Computer.Name} · сессия {location.Session.Id}";

        var details = new List<string> { SessionStateText.Get(location.Session.State) };
        if (location.FromCache)
            details.Add(RelativeTimeFormatter.Format(RelativeTimeFormatter.ToUtc(location.SeenAt), DateTime.UtcNow));

        return SearchItem.CreateLocation(location.Computer.Id, location.Session.Id, title, string.Join(" · ", details));
    }

    private static SearchItem CreateComputerItem(ComputerInfo computer)
    {
        var subtitle = !string.IsNullOrWhiteSpace(computer.Description)
            ? computer.Description
            : computer.OperatingSystem;
        return SearchItem.CreateComputer(computer.Id, computer.Name, subtitle, computer.IsOnline);
    }

    private static SearchItem CreateUserItem(UserInfo user)
    {
        var parts = new List<string> { user.SamAccountName };
        if (!string.IsNullOrWhiteSpace(user.Department)) parts.Add(user.Department);
        if (user.LockedOut) parts.Add("заблокирован");
        else if (!user.Enabled) parts.Add("отключён");

        return SearchItem.CreateUser(user.SamAccountName, user.ShownName, string.Join(" · ", parts));
    }

    private IReadOnlyList<UserLocation> SafeFindCached(string query)
    {
        try
        {
            return _locator.FindCached(query);
        }
        catch (Exception)
        {
            return Array.Empty<UserLocation>();
        }
    }

    private IReadOnlyList<ComputerInfo> SafeSearchComputers(string query)
    {
        try
        {
            return _index.Search(query, MaxComputers);
        }
        catch (Exception)
        {
            return Array.Empty<ComputerInfo>();
        }
    }

    private void ResetResults()
    {
        _query = string.Empty;
        _locations = Array.Empty<UserLocation>();
        _computers = Array.Empty<ComputerInfo>();
        _foundUsers = Array.Empty<UserInfo>();
        _usersState = UsersState.None;
        SelectedItem = null;
        Items = Array.Empty<SearchItem>();
    }

    private void CancelPending()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;

        cts.Cancel();
        cts.Dispose();
    }
}
