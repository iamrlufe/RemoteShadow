using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo;

/// <summary>Наполняет <see cref="DemoData"/> правдоподобными данными. Порядок обращений к генератору случайных чисел фиксирован: данные воспроизводимы.</summary>
internal static class DemoDataGenerator
{
    /// <summary>Группа рабочих станций и сотрудников: подразделение с кодом для имён PC-ACC-014.</summary>
    private sealed record Zone(
        string Code,
        string OuDn,
        string Label,
        string Department,
        int PcWeight,
        int UserWeight,
        double FemaleShare,
        string PhonePrefix,
        string Chief,
        string[] Titles,
        string[] SharedRoles);

    private sealed record ServerRole(string Code, string Description);

    private sealed record GroupDef(string Name, string Description);

    private sealed class UserSet
    {
        public HashSet<string> UsedSam { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UsedDn { get; } = new();
        public List<UserInfo> All { get; } = new();
        public Dictionary<string, List<UserInfo>> ByZone { get; } = new();
        public HashSet<Guid> DomainAdmins { get; } = new();
        public HashSet<Guid> HelpdeskAdmins { get; } = new();
        public HashSet<Guid> Directors { get; } = new();

        /// <summary>Кто заходит на серверы по RDP: ИТ и служебные учётные записи.</summary>
        public List<UserInfo> AdminPool { get; } = new();
    }

    private static readonly string Office = $"OU=Офис,{DemoData.RootDn}";
    private static readonly string Sales = $"OU=Продажи,{DemoData.RootDn}";
    private static readonly string Warehouse = $"OU=Склад,{DemoData.RootDn}";
    private static readonly string ServersOu = $"OU=Серверы,{DemoData.RootDn}";
    private static readonly string North = $"OU=Филиал-Север,{DemoData.RootDn}";

    private static readonly Zone[] Zones =
    [
        new("ACC", $"OU=Бухгалтерия,{Office}", "Бухгалтерия", "Бухгалтерия", 12, 15, 0.85, "+7 (495) 000-",
            "Главный бухгалтер", ["Бухгалтер", "Ведущий бухгалтер", "Бухгалтер по заработной плате", "Экономист"],
            ["общий ПК", "касса", "архив"]),
        new("HR", $"OU=Отдел кадров,{Office}", "Отдел кадров", "Отдел кадров", 5, 7, 0.85, "+7 (495) 000-",
            "Начальник отдела кадров", ["Специалист по кадрам", "Инспектор по кадрам", "Менеджер по персоналу"],
            ["общий ПК", "приём документов"]),
        new("LAW", $"OU=Юристы,{Office}", "Юристы", "Юристы", 4, 5, 0.55, "+7 (495) 000-",
            "Начальник юридического отдела", ["Юрисконсульт", "Ведущий юрист", "Помощник юриста"],
            ["общий ПК", "архив договоров"]),
        new("IT", $"OU=ИТ,{Office}", "ИТ", "ИТ", 9, 10, 0.15, "+7 (495) 000-",
            "Руководитель ИТ-отдела", ["Системный администратор", "Инженер поддержки", "Сетевой инженер", "Программист"],
            ["стенд", "тестовый ПК", "мастерская"]),
        new("OFF", Office, "Офис", "Администрация", 3, 5, 0.6, "+7 (495) 000-",
            "Офис-менеджер", ["Секретарь", "Администратор ресепшн", "Помощник руководителя"],
            ["ресепшн", "переговорная 1", "переговорная 2", "приёмная"]),
        new("WH", Warehouse, "Склад", "Склад", 13, 8, 0.2, "+7 (495) 000-",
            "Заведующий складом", ["Кладовщик", "Комплектовщик", "Оператор склада", "Водитель погрузчика"],
            ["терминал выдачи", "весы", "приёмка", "терминал сборки"]),
        new("MSK", $"OU=Москва,{Sales}", "Продажи", "Продажи", 20, 24, 0.5, "+7 (495) 000-",
            "Руководитель отдела продаж", ["Менеджер по продажам", "Старший менеджер по продажам", "Аккаунт-менеджер", "Специалист колл-центра"],
            ["колл-центр", "переговорная", "демо-зона", "общий ПК"]),
        new("KZN", $"OU=Казань,{Sales}", "Продажи", "Продажи", 12, 14, 0.5, "+7 (843) 000-",
            "Руководитель направления", ["Менеджер по продажам", "Старший менеджер по продажам", "Аккаунт-менеджер"],
            ["переговорная", "общий ПК", "демо-зона"]),
        new("NRT", North, "Филиал-Север", "Филиал-Север", 11, 12, 0.45, "+7 (8152) 00-",
            "Директор филиала", ["Менеджер", "Оператор", "Специалист", "Кладовщик", "Бухгалтер-кассир"],
            ["касса", "общий ПК", "приёмная"]),
    ];

    private static readonly string[] RetiredDescriptions = ["Списан, ожидает утилизации", "Выведен из эксплуатации"];

    private static readonly string[] DirectorTitles =
        ["Генеральный директор", "Финансовый директор", "Коммерческий директор", "Технический директор"];

    private static readonly ServerRole[] ServerRoles =
    [
        new("FS", "Файловый сервер"), new("SQL", "Сервер баз данных"), new("1C", "Сервер приложений 1С"),
        new("RDS", "Сервер терминалов"), new("APP", "Сервер приложений"), new("WEB", "Веб-сервер"),
        new("MAIL", "Почтовый сервер"), new("PRINT", "Сервер печати"), new("BACKUP", "Сервер резервного копирования"),
        new("WSUS", "Сервер обновлений"), new("DHCP", "DHCP и DNS"), new("VPN", "Шлюз удалённого доступа"),
    ];

    private static readonly GroupDef[] Groups =
    [
        new("Domain Users", "Все пользователи домена"),
        new("Domain Admins", "Администраторы домена"),
        new("Бухгалтерия", "Сотрудники бухгалтерии"),
        new("Отдел кадров", "Сотрудники отдела кадров"),
        new("Юристы", "Юридический отдел"),
        new("ИТ", "Сотрудники ИТ-отдела"),
        new("Склад", "Сотрудники склада"),
        new("Продажи", "Отдел продаж: Москва и Казань"),
        new("Филиал-Север", "Сотрудники филиала «Север»"),
        new("Руководство", "Руководство компании"),
        new("VPN-Users", "Удалённый доступ по VPN"),
        new("Helpdesk", "Служба поддержки: сброс паролей и разблокировка"),
        new(DemoData.ProtectedGroupName, "Защищённые учётные записи: изменяют только администраторы домена"),
        new("1C-Users", "Доступ к базам 1С"),
        new("Printers-Office", "Печать на принтерах офиса"),
        new("Wi-Fi-Staff", "Корпоративный Wi-Fi"),
    ];

    private static readonly HashSet<string> DepartmentGroups =
        ["Бухгалтерия", "Отдел кадров", "Юристы", "ИТ", "Склад", "Продажи", "Филиал-Север", "Руководство"];

    private static readonly HashSet<string> OfficeDepartments =
        ["Бухгалтерия", "Отдел кадров", "Юристы", "ИТ", "Руководство", "Администрация"];

    public static void Fill(DemoData data, int computerCount)
    {
        var rng = new DemoRandom(data.Seed);
        var now = data.GeneratedAtUtc;

        foreach (var ou in BuildOus()) data.AddOuCore(ou);

        var users = GenerateUsers(data, rng, now, computerCount);
        GenerateGroups(data, rng, users);
        GenerateComputers(data, rng, now, computerCount, users);
    }

    private static IEnumerable<DemoOu> BuildOus()
    {
        var root = DemoData.RootDn;
        yield return new DemoOu(Office, "Офис", root);
        yield return new DemoOu($"OU=Бухгалтерия,{Office}", "Бухгалтерия", Office);
        yield return new DemoOu($"OU=Отдел кадров,{Office}", "Отдел кадров", Office);
        yield return new DemoOu($"OU=Юристы,{Office}", "Юристы", Office);
        yield return new DemoOu($"OU=ИТ,{Office}", "ИТ", Office);
        yield return new DemoOu(Warehouse, "Склад", root);
        yield return new DemoOu(Sales, "Продажи", root);
        yield return new DemoOu($"OU=Москва,{Sales}", "Москва", Sales);
        yield return new DemoOu($"OU=Казань,{Sales}", "Казань", Sales);
        yield return new DemoOu(ServersOu, "Серверы", root);
        yield return new DemoOu(North, "Филиал-Север", root);
    }

    /// <summary>Делит total по весам, остаток раздаёт по кругу с начала.</summary>
    private static int[] Allocate(int total, int[] weights)
    {
        var sum = weights.Sum();
        var result = new int[weights.Length];
        var given = 0;
        for (var i = 0; i < weights.Length; i++)
        {
            result[i] = (int)((long)total * weights[i] / sum);
            given += result[i];
        }

        for (var i = 0; given < total; i = (i + 1) % weights.Length)
        {
            result[i]++;
            given++;
        }

        return result;
    }

    // ---------- пользователи ----------

    private static UserSet GenerateUsers(DemoData data, DemoRandom rng, DateTime now, int computerCount)
    {
        var set = new UserSet();
        const int serviceAccounts = 4;
        var total = Math.Max(12, (int)Math.Round(computerCount * 120.0 / 230.0));
        var perZone = Allocate(total - serviceAccounts, Zones.Select(z => z.UserWeight).ToArray());

        foreach (var (zone, count) in Zones.Zip(perZone))
        {
            var list = new List<UserInfo>();
            set.ByZone[zone.Code] = list;

            for (var i = 0; i < count; i++)
            {
                var isDirector = zone.Code == "OFF" && i < DirectorTitles.Length;
                var title = isDirector ? DirectorTitles[i] : i == 0 ? zone.Chief : rng.Pick(zone.Titles);
                var department = isDirector ? "Руководство" : zone.Department;

                var user = CreateUser(data, rng, now, set, zone, title, department, healthy: isDirector || i == 0, adminPrefix: null);
                list.Add(user);
                if (isDirector) set.Directors.Add(user.Id);
            }
        }

        var itZone = Zones.First(z => z.Code == "IT");
        set.AdminPool.AddRange(set.ByZone["IT"].Where(u => u.Enabled && !u.LockedOut));

        for (var i = 0; i < serviceAccounts; i++)
        {
            var domainAdmin = i < 2;
            var user = CreateUser(data, rng, now, set, itZone,
                domainAdmin ? "Администратор домена" : "Системный администратор (служебная запись)",
                "ИТ", healthy: true, adminPrefix: domainAdmin ? "da-" : "adm-");
            (domainAdmin ? set.DomainAdmins : set.HelpdeskAdmins).Add(user.Id);
            set.AdminPool.Add(user);
        }

        return set;
    }

    private static UserInfo CreateUser(
        DemoData data, DemoRandom rng, DateTime now, UserSet set, Zone zone,
        string title, string department, bool healthy, string? adminPrefix)
    {
        var female = rng.Chance(zone.FemaleShare);
        var surname = rng.Pick(DemoNames.MaleSurnames);
        if (female) surname = DemoNames.FemaleSurname(surname);
        var first = rng.Pick(female ? DemoNames.FemaleNames : DemoNames.MaleNames);
        var patronymic = rng.Pick(DemoNames.MalePatronymics);
        if (female) patronymic = DemoNames.FemalePatronymic(patronymic);

        var baseLogin = adminPrefix is null
            ? DemoNames.Translit(first)[..1] + DemoNames.Translit(surname)
            : adminPrefix + DemoNames.Translit(surname);
        var login = baseLogin;
        for (var n = 2; !set.UsedSam.Add(login); n++) login = baseLogin + n;

        var suffix = adminPrefix is null ? string.Empty : " (адм.)";
        var cn = $"{surname} {first} {patronymic}{suffix}";
        var dn = $"CN={Dn.EscapeValue(cn)},{zone.OuDn}";
        for (var n = 2; !set.UsedDn.Add(Dn.Normalize(dn)); n++) dn = $"CN={Dn.EscapeValue($"{cn} {n}")},{zone.OuDn}";

        var enabled = healthy || !rng.Chance(0.06);
        var locked = !healthy && enabled && rng.Chance(0.04);
        var neverExpires = !healthy && rng.Chance(0.07);
        var ageDays = healthy ? rng.Next(1, 80) : rng.Chance(0.03) ? rng.Next(91, 131) : rng.Next(1, 89);
        var passwordSet = now.AddDays(-ageDays).AddMinutes(-rng.Next(0, 1440));
        DateTime? passwordExpires = neverExpires ? null : passwordSet.AddDays(90);
        DateTime lastLogon = !enabled ? now.AddDays(-rng.Next(60, 500))
            : rng.Chance(0.9) ? now.AddMinutes(-rng.Next(30, 72 * 60))
            : now.AddDays(-rng.Next(4, 40));
        var phone = adminPrefix is null && rng.Chance(0.9) ? $"{zone.PhonePrefix}{rng.Next(10, 100)}-{rng.Next(0, 100):D2}" : null;

        var user = new UserInfo
        {
            Id = rng.NextGuid(),
            SamAccountName = login,
            DistinguishedName = dn,
            DisplayName = $"{surname} {first[0]}. {patronymic[0]}.{suffix}",
            UserPrincipalName = $"{login}@{DemoData.DomainName}",
            Department = department,
            Title = title,
            Phone = phone,
            Mail = adminPrefix is null ? $"{login}@corp.example" : null,
            Enabled = enabled,
            LockedOut = locked,
            PasswordNeverExpires = neverExpires,
            PasswordLastSet = passwordSet,
            PasswordExpires = passwordExpires,
            LastLogon = lastLogon,
        };

        data.AddUserCore(user);
        set.All.Add(user);
        return user;
    }

    // ---------- группы ----------

    private static void GenerateGroups(DemoData data, DemoRandom rng, UserSet users)
    {
        foreach (var g in Groups)
        {
            data.AddGroupCore(new GroupInfo(g.Name, $"CN={Dn.EscapeValue(g.Name)},CN=Users,{DemoData.RootDn}", g.Description));
        }

        foreach (var user in users.All)
        {
            void Join(string group) => data.AddMemberCore(group, user.Id);

            Join("Domain Users");
            if (user.Department is { } department && DepartmentGroups.Contains(department)) Join(department);

            var domainAdmin = users.DomainAdmins.Contains(user.Id);
            var helpdeskAdmin = users.HelpdeskAdmins.Contains(user.Id);
            var director = users.Directors.Contains(user.Id);

            if (domainAdmin)
            {
                Join("Domain Admins");
                Join(DemoData.ProtectedGroupName);
            }

            if (director) Join(DemoData.ProtectedGroupName);
            if (helpdeskAdmin || user.Title == "Инженер поддержки") Join("Helpdesk");

            if (domainAdmin || helpdeskAdmin || director || user.Department == "ИТ" || rng.Chance(0.35)) Join("VPN-Users");
            if (user.Department is "Бухгалтерия" or "Склад" or "Отдел кадров" or "Продажи" && rng.Chance(0.7)) Join("1C-Users");
            if (user.Department is { } d && OfficeDepartments.Contains(d) && rng.Chance(0.9)) Join("Printers-Office");
            if (rng.Chance(0.6)) Join("Wi-Fi-Staff");
        }
    }

    // ---------- компьютеры и сессии ----------

    private static void GenerateComputers(DemoData data, DemoRandom rng, DateTime now, int computerCount, UserSet users)
    {
        var local = now.ToLocalTime();
        var todayStart = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var maxLogonAgoMin = Math.Clamp((int)(now - todayStart).TotalMinutes, 1, 600);

        var serverCount = computerCount >= 10 ? Math.Max(2, computerCount / 20) : 0;
        var perZone = Allocate(computerCount - serverCount, Zones.Select(z => z.PcWeight).ToArray());

        var denyCandidates = new List<Guid>();
        var lastLogons = new Dictionary<Guid, DateTime>();

        foreach (var (zone, pcCount) in Zones.Zip(perZone))
        {
            var candidates = users.ByZone[zone.Code].Where(u => u.Enabled && !u.LockedOut).ToList();
            var owners = new List<UserInfo>(candidates);
            rng.Shuffle(owners);
            var nextOwner = 0;

            for (var i = 1; i <= pcCount; i++)
            {
                // Выборка без возвращения: личных компьютеров ровно столько, сколько владельцев (но не больше, чем компьютеров).
                var ownersLeft = owners.Count - nextOwner;
                UserInfo? owner = null;
                if (ownersLeft > 0 && rng.Chance(Math.Min(1.0, (double)ownersLeft / (pcCount - i + 1))))
                    owner = owners[nextOwner++];

                var enabled = !rng.Chance(0.05);
                var online = enabled && rng.Chance(0.835);

                string? description;
                if (owner is not null) description = $"{zone.Label}, {ShortName(owner)}";
                else description = rng.Chance(0.08) ? null : $"{zone.Label}, {rng.Pick(zone.SharedRoles)}";
                if (!enabled && rng.Chance(0.6)) description = rng.Pick(RetiredDescriptions);

                var win11 = rng.Chance(0.66);
                var os = win11 ? "Windows 11 Pro" : "Windows 10 Pro";
                var version = win11 ? (rng.Chance(0.6) ? "10.0 (22631)" : "10.0 (26100)") : "10.0 (19045)";
                var name = $"PC-{zone.Code}-{i:D3}";

                var computer = MakeComputer(rng, now, name, zone.OuDn, description, os, version, enabled, online, hasDns: !rng.Chance(0.04));

                var sessions = new List<SessionInfo>();
                if (online)
                {
                    var roll = rng.NextDouble();
                    if (owner is not null)
                        AddWorkstationSession(rng, now, maxLogonAgoMin, sessions, lastLogons, owner, roll < 0.80, roll < 0.90);
                    else if (candidates.Count > 0)
                        AddWorkstationSession(rng, now, maxLogonAgoMin, sessions, lastLogons, rng.Pick(candidates), roll < 0.62, roll < 0.76);

                    if (sessions.Count == 0) sessions.Add(DemoData.CreateLoginScreenSession());
                    if (enabled) denyCandidates.Add(computer.Id);
                }

                data.AddComputerCore(computer, online, accessDenied: false, sessions);
            }
        }

        GenerateServers(data, rng, now, serverCount, users);

        // Пара хостов, где у админа нет прав (с ростом домена их немного больше).
        rng.Shuffle(denyCandidates);
        foreach (var id in denyCandidates.Take(2 + computerCount / 1000)) data.SetAccessDeniedCore(id, true);

        foreach (var (id, logon) in lastLogons)
        {
            var user = data.FindUserById(id);
            if (user is not null && (user.LastLogon is null || user.LastLogon < logon))
                data.ReplaceUserCore(user with { LastLogon = logon });
        }
    }

    /// <summary>Добавляет сессию пользователя на рабочую станцию: активную, отключённую либо ничего (тогда будет экран входа).</summary>
    private static void AddWorkstationSession(
        DemoRandom rng, DateTime now, int maxLogonAgoMin, List<SessionInfo> sessions,
        Dictionary<Guid, DateTime> lastLogons, UserInfo user, bool active, bool anySession)
    {
        if (!anySession) return;

        var session = UserSession(rng, now, maxLogonAgoMin, user, active, id: 1, winStation: active ? "Console" : null, clientName: null);
        sessions.Add(session);
        if (session.LogonTime is { } logon && (!lastLogons.TryGetValue(user.Id, out var known) || known < logon))
            lastLogons[user.Id] = logon;
    }

    private static void GenerateServers(DemoData data, DemoRandom rng, DateTime now, int serverCount, UserSet users)
    {
        for (var s = 0; s < serverCount; s++)
        {
            string name;
            string description;
            if (s == 0) (name, description) = ("DC01", "Контроллер домена");
            else if (s == 1) (name, description) = ("DC02", "Контроллер домена (резервный)");
            else
            {
                var index = s - 2;
                var role = ServerRoles[index % ServerRoles.Length];
                name = $"SRV-{role.Code}{index / ServerRoles.Length + 1:D2}";
                description = role.Description;
            }

            var online = s < 2 || rng.Chance(0.93);
            var server2025 = s >= 2 && rng.Chance(0.4);
            var computer = MakeComputer(rng, now, name, ServersOu, description,
                server2025 ? "Windows Server 2025 Standard" : "Windows Server 2022 Standard",
                server2025 ? "10.0 (26100)" : "10.0 (20348)",
                enabled: true, online, hasDns: true);

            var sessions = new List<SessionInfo>();
            if (online && users.AdminPool.Count > 0)
            {
                // Один администратор — одна сессия на сервере: берём без возвращения.
                var admins = new List<UserInfo>(users.AdminPool);
                rng.Shuffle(admins);
                var count = Math.Min(s < 2 ? rng.Next(0, 2) : rng.Next(2, 6), admins.Count);
                var nextId = 2;
                for (var k = 0; k < count; k++)
                {
                    var active = rng.Chance(0.65);
                    var id = nextId;
                    nextId += 1 + rng.Next(0, 2);
                    sessions.Add(UserSession(rng, now, 12 * 60, admins[k], active, id,
                        winStation: active ? $"RDP-Tcp#{rng.Next(1, 40)}" : null,
                        clientName: $"NB-IT-{rng.Next(1, 40):D3}"));
                }
            }

            data.AddComputerCore(computer, online, accessDenied: false, sessions);
        }
    }

    private static SessionInfo UserSession(
        DemoRandom rng, DateTime now, int maxLogonAgoMin, UserInfo user, bool active, int id, string? winStation, string? clientName)
    {
        var logonAgo = rng.Next(Math.Min(15, maxLogonAgoMin), maxLogonAgoMin + 1);
        int idleMin;
        if (active) idleMin = rng.Chance(0.25) ? 0 : (int)(90 * Math.Pow(rng.NextDouble(), 2.2));
        else idleMin = rng.Next(20, 600);

        return new SessionInfo
        {
            Id = id,
            UserName = user.SamAccountName,
            Domain = DemoData.NetBiosName,
            WinStationName = winStation,
            ClientName = clientName,
            State = active ? SessionState.Active : SessionState.Disconnected,
            LogonTime = now.AddMinutes(-logonAgo),
            IdleTime = TimeSpan.FromMinutes(Math.Min(idleMin, logonAgo)),
        };
    }

    private static ComputerInfo MakeComputer(
        DemoRandom rng, DateTime now, string name, string ouDn, string? description,
        string os, string version, bool enabled, bool online, bool hasDns)
    {
        DateTime? lastLogon;
        if (rng.Chance(0.01)) lastLogon = null;
        else if (!enabled) lastLogon = now.AddMinutes(-rng.Next(90 * 1440, 500 * 1440));
        else if (online) lastLogon = now.AddMinutes(-rng.Next(10, 13 * 1440));
        else lastLogon = now.AddMinutes(-rng.Next(5 * 1440, 120 * 1440));

        var r = rng.NextDouble();
        var whenChanged = r < 0.07 ? now.AddMinutes(-rng.Next(30, 36 * 60))
            : r < 0.25 ? now.AddMinutes(-rng.Next(36 * 60, 30 * 1440))
            : now.AddMinutes(-rng.Next(30 * 1440, 900 * 1440));

        return new ComputerInfo
        {
            Id = rng.NextGuid(),
            Name = name,
            DnsName = hasDns ? $"{name}.{DemoData.DomainName}" : null,
            OperatingSystem = os,
            OperatingSystemVersion = version,
            Description = description,
            DistinguishedName = $"CN={Dn.EscapeValue(name)},{ouDn}",
            LastLogon = lastLogon,
            WhenChanged = whenChanged,
            Enabled = enabled,
        };
    }

    /// <summary>«Петрова А. С.» → «Петрова А.».</summary>
    private static string ShortName(UserInfo user)
    {
        var parts = (user.DisplayName ?? user.SamAccountName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : parts[0];
    }
}
