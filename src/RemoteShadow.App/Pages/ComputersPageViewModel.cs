using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.App.Pages;

/// <summary>
/// Страница «Компьютеры»: список из <see cref="IComputerIndex"/>; кнопки для выбранного компьютера объединяют
/// модули Connection и Power (оба регистрируют действия для <see cref="ComputerTarget"/>, их строит один
/// <see cref="ActionPresenter{TTarget}"/>), а кнопки для выбранной сессии — модули Connection (shadow) и Sessions
/// (сообщение, отключение, выход), оба для <see cref="SessionTarget"/>.
/// <para>
/// Дополнительно: фильтры/сортировка (<see cref="ComputersFilter"/>, сохраняются в <see cref="AppSettings.ComputersFilter"/>),
/// избранное и пользовательские группы (<see cref="AppSettings.ComputerGroups"/>), подсети и сканирование
/// (<see cref="ISubnetScanner"/>, <see cref="AppSettings.Subnets"/>), слияние найденного с AD (<see cref="IDeviceMergeService"/>)
/// и ручной многопротокольный опрос доступности (<see cref="IComputerAvailabilityService"/> — только по кнопке, никогда
/// не запускается сам по себе для всей сети).
/// </para>
/// </summary>
public sealed partial class ComputersPageViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly IComputerIndex _index;
    private readonly ISessionService _sessions;
    private readonly INotificationService _notifications;
    private readonly ISettingsStore _settingsStore;
    private readonly IComputerAvailabilityService _availability;
    private readonly ISubnetScanner _scanner;
    private readonly IDeviceMergeService _deviceMerge;
    private readonly IDialogService _dialogs;
    private readonly ActionPresenter<ComputerTarget> _computerPresenter;
    private readonly ActionPresenter<SessionTarget> _sessionPresenter;

    /// <summary>Устройства, найденные сканированием подсетей, по ключу (IP) — история переживает повторные сканы.</summary>
    private readonly Dictionary<string, DiscoveredDevice> _discoveredByKey = new();

    private CancellationTokenSource? _sessionsCts;
    private CancellationTokenSource? _availabilityCts;
    private CancellationTokenSource? _scanCts;
    private Guid? _trackedComputerId;
    private int? _pendingSessionId;
    private bool _disposed;
    private bool _loadingFilterFromSettings;
    private bool _rebuildingList;

    public ComputersPageViewModel(
        IComputerIndex index,
        ISessionService sessions,
        IActionRunner runner,
        INotificationService notifications,
        ISettingsStore settingsStore,
        IComputerAvailabilityService availability,
        ISubnetScanner scanner,
        IDeviceMergeService deviceMerge,
        IDialogService dialogs)
    {
        _index = index;
        _sessions = sessions;
        _notifications = notifications;
        _settingsStore = settingsStore;
        _availability = availability;
        _scanner = scanner;
        _deviceMerge = deviceMerge;
        _dialogs = dialogs;
        _computerPresenter = new ActionPresenter<ComputerTarget>(runner, notifications);
        _sessionPresenter = new ActionPresenter<SessionTarget>(runner, notifications);

        _computerPresenter.PropertyChanged += OnComputerPresenterPropertyChanged;
        _sessionPresenter.PropertyChanged += OnSessionPresenterPropertyChanged;
        _index.Changed += OnIndexChanged;

        LoadFilterFromSettings();
        ReloadGroupsAndSubnets();
        RefreshComputers();
    }

    public ObservableCollection<ComputerInfo> Computers { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public ObservableCollection<ComputerGroup> Groups { get; } = new();
    public ObservableCollection<SubnetDefinition> Subnets { get; } = new();
    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = new();

    public ActionPresenter<ComputerTarget> ComputerPresenter => _computerPresenter;
    public ActionPresenter<SessionTarget> SessionPresenter => _sessionPresenter;

    public IReadOnlyList<AvailabilityFilter> AvailabilityFilterValues { get; } = Enum.GetValues<AvailabilityFilter>();
    public IReadOnlyList<ComputerSourceFilter> SourceFilterValues { get; } = Enum.GetValues<ComputerSourceFilter>();
    public IReadOnlyList<ComputersSortBy> SortByValues { get; } = Enum.GetValues<ComputersSortBy>();
    public IReadOnlyList<ProbeKind> ProbeKindValues { get; } = Enum.GetValues<ProbeKind>();

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

    // ----- фильтры и сортировка (persist в AppSettings.ComputersFilter) -------------------------------------------

    [ObservableProperty]
    private AvailabilityFilter _availabilityFilter;

    [ObservableProperty]
    private ComputerSourceFilter _sourceFilter;

    [ObservableProperty]
    private string _nameFilter = string.Empty;

    [ObservableProperty]
    private string _ipFilter = string.Empty;

    [ObservableProperty]
    private string _descriptionFilter = string.Empty;

    [ObservableProperty]
    private string _ouFilter = string.Empty;

    [ObservableProperty]
    private bool _filterRdp;

    [ObservableProperty]
    private bool _filterSmb;

    [ObservableProperty]
    private bool _filterSsh;

    [ObservableProperty]
    private bool _showDisabledAd;

    [ObservableProperty]
    private ComputersSortBy _sortBy;

    [ObservableProperty]
    private bool _sortDescending;

    // ----- избранное / группы --------------------------------------------------------------------------------------

    [ObservableProperty]
    private ComputerGroup? _selectedGroup;

    /// <summary>
    /// Группа для «+ в группу» / «− из группы». Отдельна от <see cref="SelectedGroup"/> (фильтра списка): выбор фильтра
    /// скрывал бы только что выбранный компьютер, и добавить его в пустую группу было бы нельзя.
    /// </summary>
    [ObservableProperty]
    private ComputerGroup? _membershipGroup;

    [ObservableProperty]
    private string _newGroupName = string.Empty;

    /// <summary>Показывать только избранные (временный фильтр, не сохраняется между запусками).</summary>
    [ObservableProperty]
    private bool _onlyFavorites;

    [ObservableProperty]
    private bool _isRefreshingList;

    // ----- подсети / сканирование -----------------------------------------------------------------------------------

    [ObservableProperty]
    private SubnetDefinition? _selectedSubnet;

    [ObservableProperty]
    private string _newSubnetName = string.Empty;

    [ObservableProperty]
    private string _newSubnetCidr = string.Empty;

    [ObservableProperty]
    private ProbeKind _newSubnetProbeKind = ProbeKind.Both;

    [ObservableProperty]
    private int _newSubnetTimeoutMs = 500;

    [ObservableProperty]
    private int _newSubnetParallelism = 64;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string? _scanStatus;

    /// <summary>Порог, с которого сканирование требует подтверждения (слишком большой диапазон адресов).</summary>
    public const long LargeScanWarningThreshold = 1024;

    /// <summary>Сколько компьютеров можно проверить по кнопке без подтверждения.</summary>
    public const int LargeAvailabilityWarningThreshold = 1000;

    /// <summary>Сколько компьютеров помнит список «Недавние».</summary>
    public const int MaxRecentComputers = 15;

    public bool HasSelection => SelectedComputer is not null;

    public bool IsFavorite(ComputerInfo computer) => _settingsStore.Load().FavoriteComputers.Contains(computer.Id);

    /// <summary>Избранное выбранного компьютера — для подписи кнопки-переключателя на странице.</summary>
    public bool IsSelectedFavorite => SelectedComputer is { } computer && IsFavorite(computer);

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
    partial void OnAvailabilityFilterChanged(AvailabilityFilter value) => OnFilterChanged();
    partial void OnSourceFilterChanged(ComputerSourceFilter value) => OnFilterChanged();
    partial void OnNameFilterChanged(string value) => OnFilterChanged();
    partial void OnIpFilterChanged(string value) => OnFilterChanged();
    partial void OnDescriptionFilterChanged(string value) => OnFilterChanged();
    partial void OnOuFilterChanged(string value) => OnFilterChanged();
    partial void OnFilterRdpChanged(bool value) => OnFilterChanged();
    partial void OnFilterSmbChanged(bool value) => OnFilterChanged();
    partial void OnFilterSshChanged(bool value) => OnFilterChanged();
    partial void OnShowDisabledAdChanged(bool value) => OnFilterChanged();
    partial void OnSortByChanged(ComputersSortBy value) => OnFilterChanged();
    partial void OnSortDescendingChanged(bool value) => OnFilterChanged();

    partial void OnSelectedComputerChanged(ComputerInfo? value)
    {
        if (_rebuildingList) return;

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectedFavorite));
        RebuildComputerButtons();

        var id = value?.Id;
        if (id == _trackedComputerId) return; // та же запись, просто обновились поля (например, «в сети»).

        _trackedComputerId = id;
        if (value is not null) AddToRecent(value.Id);
        _ = LoadSessionsAsync(value);
    }

    /// <summary>Выбранный компьютер попадает в «Недавние» страницы «Избранное»: не больше 15, без повторов, свежие первыми.</summary>
    private void AddToRecent(Guid id)
    {
        try
        {
            var settings = _settingsStore.Load();
            settings.RecentComputers.Remove(id);
            settings.RecentComputers.Insert(0, id);
            if (settings.RecentComputers.Count > MaxRecentComputers)
                settings.RecentComputers.RemoveRange(MaxRecentComputers, settings.RecentComputers.Count - MaxRecentComputers);
            _settingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Сохранение недавних компьютеров", ex);
        }
    }

    partial void OnSelectedSessionChanged(SessionInfo? value) => RebuildSessionButtons();

    partial void OnNoConsentChanged(bool value) => RebuildSessionButtons();

    partial void OnOnlyFavoritesChanged(bool value) => RefreshComputers();

    partial void OnSelectedGroupChanged(ComputerGroup? value)
    {
        OnPropertyChanged(nameof(HasSelectedGroup));
        RefreshComputers();
    }

    /// <summary>Выбранная группа сужает список компьютеров: по этому признаку показывается плашка «Показать все».</summary>
    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>«Показано N из M»: видно, что фильтры или группа сузили список.</summary>
    /// <summary>
    /// Подсказка, когда список пуст из-за фильтров по протоколам или доступности: они сравнивают только с результатами
    /// проверки, а после перезапуска проверок ещё нет (доступность не хранится).
    /// </summary>
    public string? EmptyListHint =>
        Computers.Count == 0 && (FilterRdp || FilterSmb || FilterSsh || AvailabilityFilter is AvailabilityFilter.Online or AvailabilityFilter.Offline)
        && _index.All.Any(c => c.LastCheckedUtc is null)
            ? "Список пуст: фильтры по доступности и протоколам работают по результатам проверки, а часть компьютеров ещё не проверена. Нажмите «Обновить доступность»."
            : null;

    public string ListSummary => $"Показано {Computers.Count} из {_index.All.Count}";

    /// <summary>Снимает выбор группы и возвращает полный список компьютеров (без Ctrl+клика по списку групп).</summary>
    [RelayCommand]
    private void ClearGroupFilter() => SelectedGroup = null;

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

    /// <summary>Компьютеры после поиска, группы и «только избранные» — до фильтров по доступности, протоколам и IP.</summary>
    private IEnumerable<ComputerInfo> BaseSource()
    {
        var text = SearchText.Trim();
        IEnumerable<ComputerInfo> source = text.Length == 0 ? _index.All : _index.Search(text, 200);

        if (SelectedGroup is { } group) source = source.Where(c => group.ComputerIds.Contains(c.Id));

        if (OnlyFavorites)
        {
            var favorites = _settingsStore.Load().FavoriteComputers.ToHashSet();
            source = source.Where(c => favorites.Contains(c.Id));
        }

        return source;
    }

    /// <summary>
    /// Область для «Обновить доступность»: всё, что пользователь выбрал поиском, группой, подразделением и т. п., но
    /// БЕЗ фильтров по доступности, протоколам и IP. Эти фильтры сравнивают с результатом проверки, поэтому выключенный
    /// ранее компьютер при фильтре «В сети» не входил бы в видимый список и никогда не перепроверялся.
    /// </summary>
    private List<ComputerInfo> AvailabilityScope()
    {
        var state = CurrentFilterState();
        state.Availability = AvailabilityFilter.All;
        state.RdpFilter = null;
        state.SmbFilter = null;
        state.SshFilter = null;
        state.IpFilter = string.Empty;
        return ComputersFilter.Apply(BaseSource(), state).ToList();
    }

    /// <summary>Какие условия сейчас сужают список (для строки «Действуют условия»); null — ничего не сужает.</summary>
    public string? ActiveFiltersSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SearchText)) parts.Add($"поиск «{SearchText.Trim()}»");
            if (SelectedGroup is { } group) parts.Add($"группа «{group.Name}»");
            if (OnlyFavorites) parts.Add("только избранные");
            if (AvailabilityFilter != AvailabilityFilter.All) parts.Add($"доступность: {AvailabilityText(AvailabilityFilter)}");
            if (SourceFilter != ComputerSourceFilter.All) parts.Add($"источник: {(SourceFilter == ComputerSourceFilter.Ad ? "из AD" : "найденные сканированием")}");
            var protocols = new[] { FilterRdp ? "RDP" : null, FilterSmb ? "SMB" : null, FilterSsh ? "SSH" : null }.Where(p => p is not null).ToList();
            if (protocols.Count > 0) parts.Add("открыты " + string.Join(", ", protocols));
            if (!string.IsNullOrWhiteSpace(NameFilter)) parts.Add($"имя «{NameFilter.Trim()}»");
            if (!string.IsNullOrWhiteSpace(IpFilter)) parts.Add($"IP «{IpFilter.Trim()}»");
            if (!string.IsNullOrWhiteSpace(OuFilter)) parts.Add($"подразделение «{OuFilter.Trim()}»");
            if (!string.IsNullOrWhiteSpace(DescriptionFilter)) parts.Add($"описание «{DescriptionFilter.Trim()}»");
            if (!ShowDisabledAd) parts.Add("компьютеры, отключённые в AD, скрыты");
            return parts.Count == 0 ? null : "Действуют условия: " + string.Join("; ", parts) + ".";
        }
    }

    private static string AvailabilityText(AvailabilityFilter filter) => filter switch
    {
        AvailabilityFilter.Online => "в сети",
        AvailabilityFilter.Offline => "не в сети",
        AvailabilityFilter.Unknown => "не проверялись",
        _ => "все",
    };

    private void RefreshComputers()
    {
        var filtered = ComputersFilter.ApplyAndSort(BaseSource(), CurrentFilterState());

        var selectedId = SelectedComputer?.Id;

        // Clear() заставляет ListBox записать в SelectedComputer null; пока список перестраивается, это не смена
        // выбора (иначе сессии перечитываются, а компьютер заново попадает в «Недавние» при каждом событии индекса).
        _rebuildingList = true;
        try
        {
            Computers.Clear();
            foreach (var computer in filtered) Computers.Add(computer);
        }
        finally
        {
            _rebuildingList = false;
        }

        OnPropertyChanged(nameof(ListSummary));
        OnPropertyChanged(nameof(EmptyListHint));
        OnPropertyChanged(nameof(ActiveFiltersSummary));

        var reselected = selectedId is { } id ? Computers.FirstOrDefault(c => c.Id == id) : null;
        if (!Equals(SelectedComputer, reselected)) SelectedComputer = reselected;
        else if (reselected is null && _trackedComputerId is not null) OnSelectedComputerChanged(null);
    }

    private ComputersFilterState CurrentFilterState() => new()
    {
        Availability = AvailabilityFilter,
        Source = SourceFilter,
        NameFilter = NameFilter,
        IpFilter = IpFilter,
        DescriptionFilter = DescriptionFilter,
        OuFilter = OuFilter,
        RdpFilter = FilterRdp ? true : null,
        SmbFilter = FilterSmb ? true : null,
        SshFilter = FilterSsh ? true : null,
        ShowDisabledAd = ShowDisabledAd,
        SortBy = SortBy,
        SortDescending = SortDescending,
    };

    private void OnFilterChanged()
    {
        if (_loadingFilterFromSettings) return;

        var settings = _settingsStore.Load();
        settings.ComputersFilter = CurrentFilterState();
        _settingsStore.Save(settings);

        RefreshComputers();
    }

    private void LoadFilterFromSettings()
    {
        _loadingFilterFromSettings = true;
        try
        {
            var filter = _settingsStore.Load().ComputersFilter;
            AvailabilityFilter = filter.Availability;
            SourceFilter = filter.Source;
            NameFilter = filter.NameFilter;
            IpFilter = filter.IpFilter;
            DescriptionFilter = filter.DescriptionFilter;
            OuFilter = filter.OuFilter;
            FilterRdp = filter.RdpFilter == true;
            FilterSmb = filter.SmbFilter == true;
            FilterSsh = filter.SshFilter == true;
            ShowDisabledAd = filter.ShowDisabledAd;
            SortBy = filter.SortBy;
            SortDescending = filter.SortDescending;
        }
        finally
        {
            _loadingFilterFromSettings = false;
        }
    }

    private void ReloadGroupsAndSubnets()
    {
        var settings = _settingsStore.Load();
        var selectedGroupId = SelectedGroup?.Id;
        var membershipGroupId = MembershipGroup?.Id;
        Groups.Clear();
        foreach (var group in settings.ComputerGroups) Groups.Add(group);

        // Очистка коллекции сбрасывает выбор в списке и в ComboBox: возвращаем его по Id.
        if (selectedGroupId is { } sid) SelectedGroup = Groups.FirstOrDefault(g => g.Id == sid);
        if (membershipGroupId is { } mid) MembershipGroup = Groups.FirstOrDefault(g => g.Id == mid);

        Subnets.Clear();
        foreach (var subnet in settings.Subnets) Subnets.Add(subnet);
    }

    // ----- избранное --------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleFavorite(ComputerInfo? computer)
    {
        if (computer is null) return;

        var settings = _settingsStore.Load();
        if (!settings.FavoriteComputers.Remove(computer.Id)) settings.FavoriteComputers.Add(computer.Id);
        _settingsStore.Save(settings);
        OnPropertyChanged(nameof(Computers)); // список не меняется составом, но биндинг «избранное» должен перечитаться
        OnPropertyChanged(nameof(IsSelectedFavorite));
        if (OnlyFavorites) RefreshComputers();
    }

    // ----- группы -------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void CreateGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0)
        {
            _notifications.Error("Укажите название группы.");
            return;
        }

        if (Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _notifications.Error($"Группа «{name}» уже существует.");
            return;
        }

        var settings = _settingsStore.Load();
        var group = new ComputerGroup { Name = name };
        settings.ComputerGroups.Add(group);
        _settingsStore.Save(settings);

        Groups.Add(group);
        NewGroupName = string.Empty;
    }

    [RelayCommand]
    private void DeleteGroup(ComputerGroup? group)
    {
        if (group is null) return;

        var settings = _settingsStore.Load();
        settings.ComputerGroups.RemoveAll(g => g.Id == group.Id);
        _settingsStore.Save(settings);

        Groups.Remove(group);
        if (ReferenceEquals(SelectedGroup, group)) SelectedGroup = null;
        if (ReferenceEquals(MembershipGroup, group)) MembershipGroup = null;
        RefreshComputers();
    }

    [RelayCommand]
    private async Task RenameGroupAsync(ComputerGroup? group)
    {
        if (group is null) return;

        var newName = await _dialogs.PromptTextAsync("Переименовать группу", "Новое название:", group.Name);
        if (string.IsNullOrWhiteSpace(newName)) return;

        group.Name = newName.Trim();
        var settings = _settingsStore.Load();
        var stored = settings.ComputerGroups.FirstOrDefault(g => g.Id == group.Id);
        if (stored is not null) stored.Name = group.Name;
        _settingsStore.Save(settings);

        // ComputerGroup не реализует INotifyPropertyChanged: мутация Name на месте не обновляет уже отрисованный
        // ListBox-элемент (ObservableCollection уведомляет только о составе, не о свойствах элементов).
        // Перечитываем список из настроек, чтобы UI показал новое имя сразу, без перезапуска/прокрутки.
        var wasSelected = SelectedGroup?.Id == group.Id;
        ReloadGroupsAndSubnets();
        if (wasSelected) SelectedGroup = Groups.FirstOrDefault(g => g.Id == group.Id);
    }

    [RelayCommand]
    private void AddSelectedComputerToGroup(ComputerGroup? group)
    {
        if (group is null || SelectedComputer is not { } computer) return;
        if (group.ComputerIds.Contains(computer.Id)) return; // уже есть

        group.ComputerIds.Add(computer.Id);
        PersistGroups();
        RefreshComputers();
    }

    [RelayCommand]
    private void RemoveComputerFromGroup(ComputerGroup? group)
    {
        if (group is null || SelectedComputer is not { } computer) return;
        if (!group.ComputerIds.Remove(computer.Id)) return;

        PersistGroups();
        RefreshComputers();
    }

    private void PersistGroups()
    {
        var settings = _settingsStore.Load();
        settings.ComputerGroups = Groups.ToList();
        _settingsStore.Save(settings);
    }

    // ----- подсети -------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void AddSubnet()
    {
        var name = NewSubnetName.Trim();
        if (name.Length == 0)
        {
            _notifications.Error("Укажите название подсети.");
            return;
        }

        if (!Cidr.TryParse(NewSubnetCidr, out var newRange, out var error))
        {
            _notifications.Error($"Некорректный CIDR: {error}");
            return;
        }

        // 10.0.0.5/24 и 10.0.0.0/24 — одна и та же сеть.
        var duplicate = Subnets.FirstOrDefault(s => Cidr.TryParse(s.Cidr, out var r, out _) && r.Network == newRange.Network && r.PrefixLength == newRange.PrefixLength);
        if (duplicate is not null)
        {
            _notifications.Error($"Подсеть {newRange.NetworkAddress}/{newRange.PrefixLength} уже есть: «{duplicate.Name}».");
            return;
        }

        var subnet = new SubnetDefinition
        {
            Name = name,
            Cidr = NewSubnetCidr.Trim(),
            ProbeKind = NewSubnetProbeKind,
            TimeoutMs = Math.Max(50, NewSubnetTimeoutMs),
            Parallelism = Math.Max(1, NewSubnetParallelism),
        };

        var settings = _settingsStore.Load();
        settings.Subnets.Add(subnet);
        _settingsStore.Save(settings);

        Subnets.Add(subnet);
        NewSubnetName = string.Empty;
        NewSubnetCidr = string.Empty;
    }

    [RelayCommand]
    private void DeleteSubnet(SubnetDefinition? subnet)
    {
        if (subnet is null) return;

        var settings = _settingsStore.Load();
        settings.Subnets.RemoveAll(s => s.Id == subnet.Id);
        _settingsStore.Save(settings);

        Subnets.Remove(subnet);
        if (ReferenceEquals(SelectedSubnet, subnet)) SelectedSubnet = null;
    }

    [RelayCommand]
    private async Task ScanSubnetAsync(SubnetDefinition? subnet)
    {
        subnet ??= SelectedSubnet;
        if (subnet is null)
        {
            _notifications.Error("Выберите подсеть для сканирования.");
            return;
        }

        if (!Cidr.TryParse(subnet.Cidr, out var range, out var error))
        {
            _notifications.Error($"Некорректный CIDR подсети: {error}");
            return;
        }

        if (range.UsableCount > LargeScanWarningThreshold)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
                "Большая подсеть",
                $"В подсети «{subnet.Name}» ({subnet.Cidr}) {range.UsableCount} адресов. Сканирование может занять продолжительное время и создать заметную сетевую нагрузку. Продолжить?",
                new[] { subnet.Cidr },
                IsDangerous: true));
            if (!confirmed) return;
        }

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        IsScanning = true;
        ScanStatus = "Сканирование начато…";
        try
        {
            var progress = new Progress<ScanProgress>(p => ScanStatus = $"Проверено {p.Completed} из {p.Total} адресов…");

            // Без UiThread.Post: продолжение после await в реальном приложении и так возвращается в поток интерфейса
            // (WPF SynchronizationContext), а в тестах без него выполняется тут же — обновление видно сразу в обоих случаях.
            var seenThisScan = new HashSet<string>();
            await foreach (var device in _scanner.ScanAsync(subnet, progress, cts.Token).WithCancellation(cts.Token))
            {
                var matchedId = _deviceMerge.Match(device, _index.All);
                var merged = device with { MatchedComputerId = matchedId };

                seenThisScan.Add(merged.Key);
                _discoveredByKey[merged.Key] = merged; // обновляем существующую запись по ключу — без дублей при повторном скане

                var existingIndex = IndexOfDiscovered(merged.Key);
                if (existingIndex >= 0) DiscoveredDevices[existingIndex] = merged;
                else DiscoveredDevices.Add(merged);
            }

            // Устройства из истории этой подсети, не найденные в этот раз, помечаются как «не найдены», но не удаляются.
            var foundKeysThisScan = seenThisScan;
            foreach (var key in _discoveredByKey.Keys.ToList())
            {
                var existing = _discoveredByKey[key];
                if (existing.SubnetId != subnet.Id || foundKeysThisScan.Contains(key)) continue;

                var stale = existing with { FoundInLastScan = false, IsOnline = false };
                _discoveredByKey[key] = stale;
                var index = IndexOfDiscovered(key);
                if (index >= 0) DiscoveredDevices[index] = stale;
            }

            ScanStatus = $"Готово: найдено {foundKeysThisScan.Count} устройств.";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Сканирование отменено.";
        }
        catch (Exception ex)
        {
            ScanStatus = null;
            _notifications.Error($"Не удалось просканировать подсеть: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private int IndexOfDiscovered(string key)
    {
        for (var i = 0; i < DiscoveredDevices.Count; i++)
        {
            if (DiscoveredDevices[i].Key == key) return i;
        }

        return -1;
    }

    // ----- доступность (ручное обновление, никогда не само по себе) -------------------------------------------------

    /// <summary>«Обновить список»: повторная загрузка компьютеров из AD (полная), без проверки доступности.</summary>
    [RelayCommand]
    private async Task RefreshListAsync()
    {
        if (IsRefreshingList) return;

        IsRefreshingList = true;
        try
        {
            await _index.RefreshAsync(full: true, CancellationToken.None);
            RefreshComputers();
            if (_index.LastError is { } error) _notifications.Error($"Список обновлён с ошибкой: {error}");
            else _notifications.Success($"Список обновлён: компьютеров в AD — {_index.All.Count}");
        }
        catch (OperationCanceledException)
        {
            // Закрытие программы или смена подключения — не ошибка.
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось обновить список: {ex.Message}");
        }
        finally
        {
            IsRefreshingList = false;
        }
    }

    [RelayCommand]
    private async Task RefreshVisibleAvailabilityAsync()
    {
        var scope = AvailabilityScope();
        if (scope.Count > LargeAvailabilityWarningThreshold)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
                "Проверить много компьютеров",
                $"Будет проверено {scope.Count} компьютеров (DNS, ping, RDP/SMB/SSH). Это создаст заметную сетевую нагрузку. Сузьте область поиском, группой или подразделением либо продолжите.",
                new[] { $"{scope.Count} компьютеров" },
                IsDangerous: true));
            if (!confirmed) return;
        }

        _availabilityCts?.Cancel();
        var cts = new CancellationTokenSource();
        _availabilityCts = cts;

        try
        {
            await _availability.RefreshAsync(scope, force: true, cts.Token);
            RefreshComputers(); // не ждём throttled Changed от индекса — результат должен быть виден сразу
        }
        catch (OperationCanceledException)
        {
            // Отменено новым запросом — не ошибка.
        }
        catch (Exception ex)
        {
            _notifications.Error($"Не удалось проверить доступность: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelAvailabilityRefresh() => _availabilityCts?.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _index.Changed -= OnIndexChanged;
        _computerPresenter.PropertyChanged -= OnComputerPresenterPropertyChanged;
        _sessionPresenter.PropertyChanged -= OnSessionPresenterPropertyChanged;
        _sessionsCts?.Cancel();
        _availabilityCts?.Cancel();
        _scanCts?.Cancel();
    }
}
