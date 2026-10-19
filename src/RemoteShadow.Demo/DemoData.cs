using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo;

/// <summary>Подразделение тестового домена.</summary>
public sealed record DemoOu(string DistinguishedName, string Name, string ParentDn);

/// <summary>
/// Тестовый домен corp.local целиком в памяти: подразделения, компьютеры, пользователи, группы, сессии.
/// Данные детерминированы: один seed и одно «сейчас» дают одинаковый результат. После создания данные можно менять;
/// все операции потокобезопасны (одна блокировка). Все люди и имена вымышленные.
/// </summary>
public sealed class DemoData
{
    public const string DomainName = "corp.local";
    public const string NetBiosName = "CORP";
    public const string RootDn = "DC=corp,DC=local";
    public const string DefaultDomainController = "DC01.corp.local";

    /// <summary>Группа защищённых учётных записей: изменять их нельзя (как AdminSDHolder в AD).</summary>
    public const string ProtectedGroupName = "Protected";

    public const int DefaultComputerCount = 230;
    public const int DefaultSeed = 2026;

    private sealed class ComputerState
    {
        public bool Online;
        public bool AccessDenied;
        public List<SessionInfo> Sessions = new();
    }

    private sealed class GroupState
    {
        public GroupState(GroupInfo info) => Info = info;

        public GroupInfo Info { get; }
        public HashSet<Guid> Members { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ComputerInfo> _computers = new();
    private readonly Dictionary<Guid, ComputerState> _states = new();
    private readonly Dictionary<string, Guid> _hostIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DemoOu> _ous = new();
    private readonly Dictionary<string, DemoOu> _ouByDn = new();
    private readonly Dictionary<Guid, UserInfo> _users = new();
    private readonly Dictionary<string, Guid> _userByDn = new();
    private readonly Dictionary<string, Guid> _userBySam = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GroupState> _groups = new();
    private readonly Dictionary<string, GroupState> _groupByDn = new();
    private readonly Dictionary<string, GroupState> _groupByName = new(StringComparer.OrdinalIgnoreCase);

    private Func<DateTime> _clock = () => DateTime.UtcNow;
    private DateTime _lastStamp;
    private string _domainController = DefaultDomainController;

    private DemoData(int seed, DateTime generatedAtUtc)
    {
        Seed = seed;
        GeneratedAtUtc = generatedAtUtc;
        _lastStamp = generatedAtUtc;
    }

    /// <summary>Около 230 компьютеров, около 120 пользователей. Seed постоянный: каждый запуск показывает одно и то же.</summary>
    public static DemoData CreateDefault() => Generate(DefaultComputerCount, DefaultSeed);

    /// <summary>
    /// Нагрузочные тесты: любое число компьютеров (до десятков тысяч), пользователей пропорционально.
    /// «Сейчас» округляется вниз до минуты: два вызова подряд с одним seed дают одинаковые данные, включая даты.
    /// </summary>
    public static DemoData Generate(int computerCount, int seed)
    {
        var now = DateTime.UtcNow;
        return Generate(computerCount, seed, new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc));
    }

    /// <param name="nowUtc">«Сейчас» для дат в данных. С фиксированным значением результат полностью воспроизводим.</param>
    public static DemoData Generate(int computerCount, int seed, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(computerCount);

        var utc = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var data = new DemoData(seed, new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc));
        DemoDataGenerator.Fill(data, computerCount);
        data._lastStamp = data.GetLatestWhenChangedCore();
        return data;
    }

    public int Seed { get; }

    /// <summary>«Сейчас» на момент создания данных, UTC.</summary>
    public DateTime GeneratedAtUtc { get; }

    /// <summary>Источник времени для отметок whenChanged при изменениях. В тестах можно подменить.</summary>
    public Func<DateTime> Clock
    {
        get { lock (_gate) return _clock; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _clock = value;
        }
    }

    /// <summary>Контроллер домена, с которого «берутся» данные.</summary>
    public string DomainController
    {
        get { lock (_gate) return _domainController; }
    }

    /// <summary>Дерево подразделений. После создания не меняется.</summary>
    public IReadOnlyList<DemoOu> Ous => _ous;

    // ---------- компьютеры ----------

    public int ComputerCount
    {
        get { lock (_gate) return _computers.Count; }
    }

    /// <summary>Сколько компьютеров сейчас «в сети».</summary>
    public int OnlineCount
    {
        get { lock (_gate) return _states.Values.Count(s => s.Online); }
    }

    /// <summary>Копия списка компьютеров (в порядке создания). IsOnline в записях не заполняется: это работа <see cref="DemoHostProbe"/>.</summary>
    public IReadOnlyList<ComputerInfo> GetComputers()
    {
        lock (_gate) return _computers.Values.ToArray();
    }

    public ComputerInfo? FindComputer(Guid id)
    {
        lock (_gate) return _computers.GetValueOrDefault(id);
    }

    /// <summary>Поиск по короткому или полному имени, без учёта регистра.</summary>
    public ComputerInfo? FindComputerByHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.Trim().TrimEnd('.');

        lock (_gate)
        {
            if (_hostIndex.TryGetValue(host, out var id)) return _computers.GetValueOrDefault(id);

            var dot = host.IndexOf('.');
            if (dot > 0 && host.EndsWith("." + DomainName, StringComparison.OrdinalIgnoreCase)
                && _hostIndex.TryGetValue(host[..dot], out id))
                return _computers.GetValueOrDefault(id);

            return null;
        }
    }

    public bool IsOnline(Guid id)
    {
        lock (_gate) return _states.TryGetValue(id, out var s) && s.Online;
    }

    /// <summary>
    /// Включает или выключает компьютер. При выключении сессии пропадают; при включении рабочая станция
    /// показывает экран входа без пользователя.
    /// </summary>
    public void SetOnline(Guid id, bool online)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state) || !_computers.TryGetValue(id, out var computer)) return;

            // Отключённая учётная запись не «включается» сама (например, по таймеру возврата после перезагрузки).
            if (online && !computer.Enabled) return;

            state.Online = online;
            state.Sessions = new List<SessionInfo>();
            if (online && !IsServer(computer)) state.Sessions.Add(CreateLoginScreenSession());
        }
    }

    /// <summary>true — подключение к хосту завершается ошибкой «нет прав» (как будто у админа нет прав на этом компьютере).</summary>
    public bool IsAccessDenied(Guid id)
    {
        lock (_gate) return _states.TryGetValue(id, out var s) && s.AccessDenied;
    }

    public void SetAccessDenied(Guid id, bool denied)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(id, out var s)) s.AccessDenied = denied;
        }
    }

    public IReadOnlyList<SessionInfo> GetSessions(Guid computerId)
    {
        lock (_gate) return _states.TryGetValue(computerId, out var s) ? s.Sessions.ToArray() : Array.Empty<SessionInfo>();
    }

    public void SetSessions(Guid computerId, IEnumerable<SessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        lock (_gate)
        {
            if (_states.TryGetValue(computerId, out var s)) s.Sessions = sessions.ToList();
        }
    }

    /// <summary>Консоль на экране входа: пользователя нет.</summary>
    public static SessionInfo CreateLoginScreenSession() =>
        new() { Id = 1, WinStationName = "Console", State = SessionState.Connected };

    internal bool RemoveSession(Guid computerId, int sessionId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(computerId, out var s) && s.Sessions.RemoveAll(x => x.Id == sessionId) > 0;
        }
    }

    internal bool UpdateSession(Guid computerId, int sessionId, Func<SessionInfo, SessionInfo> update)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(computerId, out var s)) return false;
            var index = s.Sessions.FindIndex(x => x.Id == sessionId);
            if (index < 0) return false;
            s.Sessions[index] = update(s.Sessions[index]);
            return true;
        }
    }

    internal ComputerInfo AddComputer(string name, string ouDn, string? description, string? operatingSystem, bool online, bool enabled)
    {
        name = HostNameValidator.Require(name);
        if (name.Contains('.')) throw new ArgumentException("Нужно короткое имя компьютера.", nameof(name));

        lock (_gate)
        {
            if (!_ouByDn.TryGetValue(Dn.Normalize(ouDn), out var ou))
                throw new ArgumentException($"Нет подразделения {ouDn}.", nameof(ouDn));
            if (_hostIndex.ContainsKey(name))
                throw new InvalidOperationException($"Компьютер {name} уже существует.");

            var server = operatingSystem?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true;
            var computer = new ComputerInfo
            {
                Id = Guid.NewGuid(),
                Name = name,
                DnsName = $"{name}.{DomainName}",
                OperatingSystem = operatingSystem,
                OperatingSystemVersion = operatingSystem is null ? null : server ? "10.0 (20348)" : "10.0 (26100)",
                Description = description,
                DistinguishedName = $"CN={Dn.EscapeValue(name)},{ou.DistinguishedName}",
                Enabled = enabled,
                WhenChanged = NextStamp(),
            };

            AddComputerCore(computer, online && enabled, false);
            if (online && enabled && !server) _states[computer.Id].Sessions.Add(CreateLoginScreenSession());
            return computer;
        }
    }

    internal ComputerInfo RenameComputer(Guid id, string newName)
    {
        newName = HostNameValidator.Require(newName);
        if (newName.Contains('.')) throw new ArgumentException("Нужно короткое имя компьютера.", nameof(newName));

        lock (_gate)
        {
            var old = GetComputerOrThrow(id);
            if (_hostIndex.TryGetValue(newName, out var other) && other != id)
                throw new InvalidOperationException($"Компьютер {newName} уже существует.");

            string? dnsName = null;
            if (old.DnsName is not null)
            {
                var dot = old.DnsName.IndexOf('.');
                dnsName = newName + (dot >= 0 ? old.DnsName[dot..] : string.Empty);
            }

            UnindexHost(old);
            var updated = old with
            {
                Name = newName,
                DnsName = dnsName,
                DistinguishedName = $"CN={Dn.EscapeValue(newName)},{old.ParentDn}",
                WhenChanged = NextStamp(),
            };
            _computers[id] = updated;
            IndexHost(updated);
            return updated;
        }
    }

    internal ComputerInfo MoveComputer(Guid id, string newOuDn)
    {
        lock (_gate)
        {
            var old = GetComputerOrThrow(id);
            if (!_ouByDn.TryGetValue(Dn.Normalize(newOuDn), out var ou))
                throw new ArgumentException($"Нет подразделения {newOuDn}.", nameof(newOuDn));

            var updated = old with
            {
                DistinguishedName = $"CN={Dn.EscapeValue(old.Name)},{ou.DistinguishedName}",
                WhenChanged = NextStamp(),
            };
            _computers[id] = updated;
            return updated;
        }
    }

    internal ComputerInfo SetComputerEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            var old = GetComputerOrThrow(id);
            var updated = old with { Enabled = enabled, WhenChanged = NextStamp() };
            _computers[id] = updated;
            if (!enabled)
            {
                var state = _states[id];
                state.Online = false;
                state.Sessions = new List<SessionInfo>();
            }

            return updated;
        }
    }

    internal bool RemoveComputer(Guid id)
    {
        lock (_gate)
        {
            if (!_computers.TryGetValue(id, out var old)) return false;
            UnindexHost(old);
            _computers.Remove(id);
            _states.Remove(id);
            return true;
        }
    }

    internal void SwitchDomainController(string name, bool bumpWhenChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            _domainController = name;
            if (!bumpWhenChanged) return;

            // whenChanged не реплицируется: на другом контроллере у записей другие отметки.
            var stamp = NextStamp();
            foreach (var id in _computers.Keys.ToArray())
                _computers[id] = _computers[id] with { WhenChanged = stamp };
        }
    }

    // ---------- пользователи и группы ----------

    public IReadOnlyList<UserInfo> GetUsers()
    {
        lock (_gate) return _users.Values.ToArray();
    }

    public UserInfo? FindUserById(Guid id)
    {
        lock (_gate) return _users.GetValueOrDefault(id);
    }

    public UserInfo? FindUserBySamAccountName(string? sam)
    {
        if (string.IsNullOrWhiteSpace(sam)) return null;
        lock (_gate) return _userBySam.TryGetValue(sam.Trim(), out var id) ? _users[id] : null;
    }

    public UserInfo? FindUserByDn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        lock (_gate) return _userByDn.TryGetValue(Dn.Normalize(dn), out var id) ? _users[id] : null;
    }

    public IReadOnlyList<GroupInfo> GetGroups()
    {
        lock (_gate) return _groups.Select(g => g.Info).ToArray();
    }

    public GroupInfo? FindGroupByDn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        lock (_gate) return _groupByDn.TryGetValue(Dn.Normalize(dn), out var g) ? g.Info : null;
    }

    /// <summary>Прямое членство пользователя.</summary>
    public IReadOnlyList<GroupInfo> GetGroupsOf(Guid userId)
    {
        lock (_gate) return _groups.Where(g => g.Members.Contains(userId)).Select(g => g.Info).ToArray();
    }

    public IReadOnlyList<UserInfo> GetGroupMembers(string groupDn)
    {
        lock (_gate)
        {
            if (!_groupByDn.TryGetValue(Dn.Normalize(groupDn), out var g)) return Array.Empty<UserInfo>();
            return g.Members.Where(_users.ContainsKey).Select(id => _users[id]).ToArray();
        }
    }

    public bool IsMember(Guid userId, string groupName)
    {
        lock (_gate) return _groupByName.TryGetValue(groupName, out var g) && g.Members.Contains(userId);
    }

    /// <summary>true — учётная запись входит в группу «Protected»: изменять её нельзя.</summary>
    public bool IsProtected(Guid userId) => IsMember(userId, ProtectedGroupName);

    internal UserInfo UpdateUser(Guid id, Func<UserInfo, UserInfo> update)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var old)) throw new KeyNotFoundException($"Нет пользователя {id}.");
            var updated = update(old);
            _users[id] = updated;
            return updated;
        }
    }

    /// <summary>false — уже входит в группу или группы нет.</summary>
    internal bool AddMember(string groupDn, Guid userId)
    {
        lock (_gate)
        {
            return _groupByDn.TryGetValue(Dn.Normalize(groupDn), out var g) && _users.ContainsKey(userId) && g.Members.Add(userId);
        }
    }

    internal bool RemoveMember(string groupDn, Guid userId)
    {
        lock (_gate)
        {
            return _groupByDn.TryGetValue(Dn.Normalize(groupDn), out var g) && g.Members.Remove(userId);
        }
    }

    // ---------- заполнение при создании (до публикации объекта, без блокировок) ----------

    internal void AddOuCore(DemoOu ou)
    {
        _ous.Add(ou);
        _ouByDn[Dn.Normalize(ou.DistinguishedName)] = ou;
    }

    internal void AddComputerCore(ComputerInfo computer, bool online, bool accessDenied, IEnumerable<SessionInfo>? sessions = null)
    {
        _computers[computer.Id] = computer;
        _states[computer.Id] = new ComputerState
        {
            Online = online,
            AccessDenied = accessDenied,
            Sessions = sessions?.ToList() ?? new List<SessionInfo>(),
        };
        IndexHost(computer);
    }

    internal void SetAccessDeniedCore(Guid id, bool denied) => _states[id].AccessDenied = denied;

    internal void AddUserCore(UserInfo user)
    {
        _users[user.Id] = user;
        _userByDn[Dn.Normalize(user.DistinguishedName)] = user.Id;
        _userBySam[user.SamAccountName] = user.Id;
    }

    internal void ReplaceUserCore(UserInfo user) => _users[user.Id] = user;

    internal void AddGroupCore(GroupInfo group)
    {
        var state = new GroupState(group);
        _groups.Add(state);
        _groupByDn[Dn.Normalize(group.DistinguishedName)] = state;
        _groupByName[group.Name] = state;
    }

    internal void AddMemberCore(string groupName, Guid userId) => _groupByName[groupName].Members.Add(userId);

    // ---------- внутреннее ----------

    private static bool IsServer(ComputerInfo computer) =>
        computer.OperatingSystem?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Строго возрастающая отметка whenChanged. Вызывать под блокировкой.</summary>
    private DateTime NextStamp()
    {
        var now = _clock();
        now = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : DateTime.SpecifyKind(now, DateTimeKind.Utc);
        if (now <= _lastStamp) now = _lastStamp.AddTicks(1);
        _lastStamp = now;
        return now;
    }

    private DateTime GetLatestWhenChangedCore() =>
        _computers.Values.Select(c => c.WhenChanged ?? GeneratedAtUtc).DefaultIfEmpty(GeneratedAtUtc).Max();

    private ComputerInfo GetComputerOrThrow(Guid id) =>
        _computers.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"Нет компьютера {id}.");

    private void IndexHost(ComputerInfo computer)
    {
        _hostIndex[computer.Name] = computer.Id;
        if (!string.IsNullOrWhiteSpace(computer.DnsName)) _hostIndex[computer.DnsName] = computer.Id;
    }

    private void UnindexHost(ComputerInfo computer)
    {
        _hostIndex.Remove(computer.Name);
        if (!string.IsNullOrWhiteSpace(computer.DnsName)) _hostIndex.Remove(computer.DnsName);
    }
}
