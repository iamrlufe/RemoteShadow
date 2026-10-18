using System.Diagnostics;
using System.Text.RegularExpressions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo.Tests;

public class DemoDataTests
{
    [Fact]
    public void SameSeedAndNow_GiveIdenticalData()
    {
        var a = DemoData.Generate(300, 7, TestSupport.Now);
        var b = DemoData.Generate(300, 7, TestSupport.Now);

        Assert.Equal(a.GetComputers(), b.GetComputers());
        Assert.Equal(a.GetUsers(), b.GetUsers());
        Assert.Equal(a.GetGroups(), b.GetGroups());
        Assert.Equal(a.Ous, b.Ous);

        foreach (var c in a.GetComputers())
        {
            Assert.Equal(a.GetSessions(c.Id), b.GetSessions(c.Id));
            Assert.Equal(a.IsOnline(c.Id), b.IsOnline(c.Id));
            Assert.Equal(a.IsAccessDenied(c.Id), b.IsAccessDenied(c.Id));
        }

        foreach (var g in a.GetGroups())
        {
            Assert.Equal(
                a.GetGroupMembers(g.DistinguishedName).Select(u => u.Id).Order(),
                b.GetGroupMembers(g.DistinguishedName).Select(u => u.Id).Order());
        }
    }

    [Fact]
    public void DifferentSeed_GivesDifferentData()
    {
        var a = DemoData.Generate(300, 1, TestSupport.Now);
        var b = DemoData.Generate(300, 2, TestSupport.Now);

        Assert.NotEqual(a.GetComputers().Select(c => c.Id), b.GetComputers().Select(c => c.Id));
        Assert.NotEqual(a.GetUsers().Select(u => u.SamAccountName), b.GetUsers().Select(u => u.SamAccountName));
    }

    [Fact]
    public void TwoArgumentOverload_GivesIdenticalData()
    {
        // «Сейчас» округляется до минуты: если вызовы пришлись на границу минуты, пробуем ещё раз.
        DemoData a, b;
        var attempts = 0;
        do
        {
            a = DemoData.Generate(120, 5);
            b = DemoData.Generate(120, 5);
        }
        while (a.GeneratedAtUtc != b.GeneratedAtUtc && ++attempts < 5);

        Assert.Equal(a.GeneratedAtUtc, b.GeneratedAtUtc);
        Assert.Equal(0, a.GeneratedAtUtc.Second);
        Assert.Equal(a.GetComputers(), b.GetComputers());
        Assert.Equal(a.GetUsers(), b.GetUsers());
    }

    [Fact]
    public void Generate_NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DemoData.Generate(-1, 1, TestSupport.Now));
    }

    [Fact]
    public void ObjectGuids_AreUniqueAcrossComputersAndUsers()
    {
        var data = TestSupport.Data(2000, 11);
        var ids = data.GetComputers().Select(c => c.Id).Concat(data.GetUsers().Select(u => u.Id)).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
    }

    [Fact]
    public void Dates_AreReasonableAndUtc()
    {
        var data = TestSupport.Data(2000, 1);
        var computers = data.GetComputers();

        Assert.All(computers.Where(c => c.LastLogon is not null), c => Assert.Equal(DateTimeKind.Utc, c.LastLogon!.Value.Kind));
        Assert.Contains(computers, c => c.LastLogon is null);

        // whenChanged разный: свежие изменения (для частичного обновления) и давние.
        Assert.Contains(computers, c => c.WhenChanged > TestSupport.Now.AddDays(-2));
        Assert.Contains(computers, c => c.WhenChanged < TestSupport.Now.AddDays(-90));

        // Отключённые компьютеры давно не входили в домен.
        Assert.All(computers.Where(c => !c.Enabled && c.LastLogon is not null), c => Assert.True(c.LastLogon < TestSupport.Now.AddDays(-60)));
    }

    [Fact]
    public void SetOnline_ResetsSessions_AndShowsLoginScreenForWorkstation()
    {
        var data = TestSupport.Data();
        var pc = TestSupport.OnlineWorkstationWith(data, SessionState.Active);

        data.SetOnline(pc.Id, false);
        Assert.False(data.IsOnline(pc.Id));
        Assert.Empty(data.GetSessions(pc.Id));

        data.SetOnline(pc.Id, true);
        var session = Assert.Single(data.GetSessions(pc.Id));
        Assert.False(session.HasUser);
        Assert.Equal(SessionState.Connected, session.State);
    }

    [Fact]
    public void CreateDefault_HasExpectedShape()
    {
        var data = DemoData.CreateDefault();

        Assert.Equal(230, data.ComputerCount);
        Assert.InRange(data.GetUsers().Count, 100, 140);
        Assert.InRange(data.GetGroups().Count, 14, 18);
        Assert.Equal("DC01.corp.local", data.DomainController);

        var ouNames = data.Ous.Select(o => o.Name).ToList();
        foreach (var expected in new[] { "Офис", "Бухгалтерия", "Отдел кадров", "Юристы", "ИТ", "Склад", "Продажи", "Москва", "Казань", "Серверы", "Филиал-Север" })
            Assert.Contains(expected, ouNames);

        // Вложенность: в «Офис» входят четыре отдела, в «Продажи» — два города.
        string[] ChildrenOf(string name)
        {
            var parent = data.Ous.First(x => x.Name == name).DistinguishedName;
            return data.Ous.Where(o => Dn.AreEqual(o.ParentDn, parent)).Select(o => o.Name).Order(StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(new[] { "Бухгалтерия", "Отдел кадров", "ИТ", "Юристы" }.Order(StringComparer.Ordinal), ChildrenOf("Офис"));
        Assert.Equal(new[] { "Казань", "Москва" }, ChildrenOf("Продажи"));
    }

    [Fact]
    public void Generate_ZeroAndTinyCounts_Work()
    {
        var empty = DemoData.Generate(0, 1, TestSupport.Now);
        Assert.Equal(0, empty.ComputerCount);
        Assert.NotEmpty(empty.GetUsers());

        var tiny = DemoData.Generate(5, 1, TestSupport.Now);
        Assert.Equal(5, tiny.ComputerCount);
        Assert.DoesNotContain(tiny.GetComputers(), TestSupport.IsServer);

        var small = DemoData.Generate(10, 1, TestSupport.Now);
        Assert.Equal(10, small.ComputerCount);
        Assert.Equal(2, small.GetComputers().Count(TestSupport.IsServer));
    }

    [Fact]
    public void Computers_AreConsistent()
    {
        var data = TestSupport.Data(1500, 3);
        var computers = data.GetComputers();
        var ouDns = data.Ous.Select(o => Dn.Normalize(o.DistinguishedName)).ToHashSet();

        Assert.Equal(1500, computers.Count);
        Assert.Equal(computers.Count, computers.Select(c => c.Id).Distinct().Count());
        Assert.Equal(computers.Count, computers.Select(c => c.Name.ToUpperInvariant()).Distinct().Count());
        Assert.Equal(computers.Count, computers.Select(c => Dn.Normalize(c.DistinguishedName)).Distinct().Count());

        var dnsNames = computers.Where(c => c.DnsName is not null).Select(c => c.DnsName!.ToUpperInvariant()).ToList();
        Assert.Equal(dnsNames.Count, dnsNames.Distinct().Count());

        foreach (var c in computers)
        {
            Assert.Contains(Dn.Normalize(c.ParentDn), ouDns);
            Assert.Equal(c.Name, Dn.GetName(c.DistinguishedName));
            Assert.StartsWith("CN=", c.DistinguishedName);
            Assert.EndsWith("," + DemoData.RootDn, c.DistinguishedName);
            Assert.True(HostNameValidator.IsValid(c.HostName));
            Assert.Equal(DateTimeKind.Utc, c.WhenChanged!.Value.Kind);
            Assert.True(c.WhenChanged <= TestSupport.Now);
            if (c.LastLogon is { } logon) Assert.True(logon <= TestSupport.Now);
            if (c.DnsName is not null) Assert.Equal(c.Name + "." + DemoData.DomainName, c.DnsName, ignoreCase: true);
        }

        foreach (var ou in data.Ous)
            Assert.True(ou.ParentDn == DemoData.RootDn || ouDns.Contains(Dn.Normalize(ou.ParentDn)));
    }

    [Fact]
    public void Computers_LookRealistic_InDefaultDomain()
    {
        var data = TestSupport.Data();
        var computers = data.GetComputers();
        var workstations = computers.Where(c => !TestSupport.IsServer(c)).ToList();
        var servers = computers.Where(TestSupport.IsServer).ToList();

        Assert.All(workstations, c => Assert.Matches(@"^PC-[A-Z]+-\d{3,}$", c.Name));
        Assert.All(workstations, c => Assert.Contains(c.OperatingSystem, new[] { "Windows 11 Pro", "Windows 10 Pro" }));
        Assert.Contains(workstations, c => c.OperatingSystem == "Windows 11 Pro");
        Assert.Contains(workstations, c => c.OperatingSystem == "Windows 10 Pro");

        Assert.InRange(servers.Count, 8, 16);
        Assert.Contains(servers, c => c.OperatingSystem!.StartsWith("Windows Server 2022"));
        Assert.Contains(servers, c => c.OperatingSystem!.StartsWith("Windows Server 2025"));
        Assert.All(servers, c => Assert.NotNull(c.DnsName));
        Assert.Contains(servers, c => c.Name == "DC01" && c.DnsName == DemoData.DefaultDomainController);

        Assert.Contains(computers, c => !c.Enabled);
        Assert.Contains(computers, c => c.DnsName is null);
        Assert.Contains(computers, c => c.Description is null);

        var example = computers.First(c => c.Name == "PC-ACC-014");
        Assert.StartsWith("Бухгалтерия", example.Description ?? string.Empty);
        Assert.Contains(computers, c => Regex.IsMatch(c.Description ?? string.Empty, @"^Бухгалтерия, \p{L}+ \p{L}\.$"));

        // Компьютеры лежат во всех подразделениях, кроме «Продажи»: там только вложенные города.
        foreach (var ou in data.Ous.Where(o => o.Name != "Продажи"))
            Assert.Contains(computers, c => Dn.AreEqual(c.ParentDn, ou.DistinguishedName));
    }

    [Theory]
    [InlineData(230)]
    [InlineData(10000)]
    public void Online_IsAboutEightyPercent(int count)
    {
        var data = TestSupport.Data(count, 4);
        var share = (double)data.OnlineCount / data.ComputerCount;

        Assert.InRange(share, 0.72, 0.88);
        Assert.All(data.GetComputers().Where(c => !c.Enabled), c => Assert.False(data.IsOnline(c.Id)));
    }

    [Fact]
    public void Sessions_MatchDescription()
    {
        var data = TestSupport.Data();
        var computers = data.GetComputers();
        var onlineWorkstations = computers.Where(c => !TestSupport.IsServer(c) && data.IsOnline(c.Id)).ToList();

        var states = onlineWorkstations.Select(c => data.GetSessions(c.Id)).ToList();
        var active = states.Count(s => s.Any(x => x.State == SessionState.Active && x.HasUser));
        var disconnected = states.Count(s => s.Any(x => x.State == SessionState.Disconnected && x.HasUser));
        var empty = states.Count(s => s.All(x => !x.HasUser));

        Assert.True(active > onlineWorkstations.Count / 2, "У большинства включённых станций должна быть активная сессия.");
        Assert.True(disconnected > 0);
        Assert.True(empty > 0);
        Assert.All(states, s => Assert.True(s.Count(x => x.HasUser) <= 1));
        Assert.All(states, s => Assert.NotEmpty(s));

        // Нет сессий у выключенных.
        foreach (var c in computers.Where(c => !data.IsOnline(c.Id)))
            Assert.Empty(data.GetSessions(c.Id));

        // Активные сессии: вход недавно, простой 0–90 минут, пользователь существует в AD.
        foreach (var s in states.SelectMany(x => x).Where(x => x.State == SessionState.Active))
        {
            Assert.True(s.HasUser);
            Assert.Equal("CORP", s.Domain);
            Assert.NotNull(data.FindUserBySamAccountName(s.UserName));
            Assert.InRange(s.LogonTime!.Value, TestSupport.Now.AddHours(-24), TestSupport.Now);
            Assert.InRange(s.IdleTime!.Value, TimeSpan.Zero, TimeSpan.FromMinutes(90));
            Assert.True(s.CanShadow);
        }
    }

    [Fact]
    public void Servers_HaveSeveralRdpSessions()
    {
        var data = TestSupport.Data();
        var servers = data.GetComputers().Where(c => TestSupport.IsServer(c) && data.IsOnline(c.Id) && !c.Name.StartsWith("DC", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(servers);
        foreach (var server in servers)
        {
            var sessions = data.GetSessions(server.Id);
            Assert.InRange(sessions.Count, 2, 5);
            Assert.All(sessions, s => Assert.True(s.HasUser));
            Assert.All(sessions.Where(s => s.State == SessionState.Active), s => Assert.StartsWith("RDP-Tcp#", s.WinStationName));
            Assert.All(sessions, s => Assert.False(string.IsNullOrEmpty(s.ClientName)));
            Assert.Equal(sessions.Count, sessions.Select(s => s.Id).Distinct().Count());
        }
    }

    [Fact]
    public void SomeHosts_DenyAccess()
    {
        var data = TestSupport.Data();
        var denied = data.GetComputers().Where(c => data.IsAccessDenied(c.Id)).ToList();

        Assert.InRange(denied.Count, 2, 4);
        Assert.All(denied, c => Assert.True(data.IsOnline(c.Id)));
    }

    [Fact]
    public void Users_AreConsistentAndRussian()
    {
        var data = TestSupport.Data();
        var users = data.GetUsers();
        var ouDns = data.Ous.Select(o => Dn.Normalize(o.DistinguishedName)).ToHashSet();

        Assert.Equal(users.Count, users.Select(u => u.Id).Distinct().Count());
        Assert.Equal(users.Count, users.Select(u => u.SamAccountName.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(users.Count, users.Select(u => Dn.Normalize(u.DistinguishedName)).Distinct().Count());

        foreach (var u in users)
        {
            Assert.Matches(@"^[a-z][a-z0-9-]*$", u.SamAccountName);
            Assert.Matches(@"^\p{L}+ \p{L}\. \p{L}\.( \(адм\.\))?$", u.DisplayName!);
            Assert.Equal($"{u.SamAccountName}@corp.local", u.UserPrincipalName);
            Assert.Contains(Dn.Normalize(u.DistinguishedName.Split(',', 2)[1]), ouDns);
            if (u.Phone is not null) Assert.Matches(@"^\+7 \(\d{3,4}\) \d{2,3}-\d{2}-\d{2}$", u.Phone);
            if (u.Mail is not null) Assert.Equal($"{u.SamAccountName}@corp.example", u.Mail);
            Assert.False(string.IsNullOrWhiteSpace(u.Department));
            Assert.False(string.IsNullOrWhiteSpace(u.Title));
            Assert.Equal(u.Id, data.FindUserByDn(u.DistinguishedName.ToUpperInvariant())!.Id);
            Assert.Equal(u.Id, data.FindUserBySamAccountName(u.SamAccountName.ToUpperInvariant())!.Id);
        }

        Assert.Contains(users, u => !u.Enabled);
        Assert.Contains(users, u => u.LockedOut);
        Assert.Contains(users, u => u.PasswordNeverExpires);
        Assert.Contains(users, u => u.PasswordExpires is { } e && e < TestSupport.Now);
        Assert.Contains(users, u => u.SamAccountName.StartsWith("da-", StringComparison.Ordinal));
        Assert.All(users.Where(u => u.LockedOut), u => Assert.True(u.Enabled));
        Assert.All(users.Where(u => u.PasswordNeverExpires), u => Assert.Null(u.PasswordExpires));
        Assert.All(users.Where(u => !u.PasswordNeverExpires), u => Assert.Equal(u.PasswordLastSet!.Value.AddDays(90), u.PasswordExpires));
    }

    [Fact]
    public void Groups_HaveMembership()
    {
        var data = TestSupport.Data();
        var groups = data.GetGroups();
        var users = data.GetUsers();
        var names = groups.Select(g => g.Name).ToList();

        foreach (var expected in new[] { "Domain Users", "Domain Admins", "Бухгалтерия", "VPN-Users", "Helpdesk", "Protected" })
            Assert.Contains(expected, names);

        Assert.Equal(groups.Count, groups.Select(g => Dn.Normalize(g.DistinguishedName)).Distinct().Count());
        Assert.All(users, u => Assert.True(data.IsMember(u.Id, "Domain Users")));
        Assert.All(groups, g => Assert.NotEmpty(data.GetGroupMembers(g.DistinguishedName)));

        // Защищённые: руководство и администраторы домена.
        var protectedUsers = users.Where(u => data.IsProtected(u.Id)).ToList();
        Assert.InRange(protectedUsers.Count, 2, 10);
        Assert.All(protectedUsers, u => Assert.False(u.LockedOut));

        foreach (var u in users.Where(u => u.Department == "Бухгалтерия"))
            Assert.True(data.IsMember(u.Id, "Бухгалтерия"));
        foreach (var u in users)
            Assert.NotEmpty(data.GetGroupsOf(u.Id));
    }

    [Fact]
    public void Generate_TenThousand_IsFast()
    {
        _ = DemoData.Generate(50, 1, TestSupport.Now); // прогрев JIT

        var watch = Stopwatch.StartNew();
        var data = DemoData.Generate(10_000, 9, TestSupport.Now);
        watch.Stop();

        Assert.Equal(10_000, data.ComputerCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Генерация заняла {watch.Elapsed.TotalMilliseconds:0} мс.");
    }

    [Fact]
    public void ConcurrentAccess_DoesNotCorruptState()
    {
        var data = TestSupport.Data(500, 2);
        var ids = data.GetComputers().Select(c => c.Id).ToArray();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 4000, i =>
        {
            try
            {
                var id = ids[i % ids.Length];
                data.SetOnline(id, i % 3 != 0);
                _ = data.GetSessions(id);
                _ = data.GetComputers().Count;
                _ = data.OnlineCount;
                _ = data.FindComputerByHost(data.FindComputer(id)!.Name);
                data.SetAccessDenied(id, i % 7 == 0);
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        });

        Assert.Empty(exceptions);
        Assert.Equal(500, data.ComputerCount);
    }

    [Fact]
    public void FindComputerByHost_AcceptsShortAndFullNamesInAnyCase()
    {
        var data = TestSupport.Data();
        var computer = data.GetComputers().First(c => c.DnsName is not null);

        Assert.Equal(computer.Id, data.FindComputerByHost(computer.Name.ToLowerInvariant())!.Id);
        Assert.Equal(computer.Id, data.FindComputerByHost(computer.DnsName!.ToLowerInvariant())!.Id);
        Assert.Equal(computer.Id, data.FindComputerByHost(computer.DnsName + ".")!.Id);
        Assert.Null(data.FindComputerByHost("no-such-host"));
        Assert.Null(data.FindComputerByHost(computer.Name + ".other.local"));
        Assert.Null(data.FindComputerByHost(" "));

        // Компьютер без DNS-имени доступен и по полному имени домена: мы не обязаны знать, зарегистрирован ли он в DNS.
        var noDns = data.GetComputers().First(c => c.DnsName is null);
        Assert.Equal(noDns.Id, data.FindComputerByHost(noDns.Name + ".corp.local")!.Id);
    }
}
