using System.Diagnostics;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo.Tests;

public class DemoComputerDirectoryTests
{
    private const string Office = "OU=Офис,DC=corp,DC=local";
    private const string Warehouse = "OU=Склад,DC=corp,DC=local";

    private static (DemoData Data, DemoComputerDirectory Dir, FakeClock Clock) Create(int count = 230, int seed = 1)
    {
        var data = TestSupport.Data(count, seed);
        var clock = new FakeClock(TestSupport.Now.AddHours(1));
        data.Clock = clock.Read;
        return (data, new DemoComputerDirectory(data, DemoTimings.Instant), clock);
    }

    private static Task<List<ComputerInfo>> All(DemoComputerDirectory dir, ComputerQuery? query = null) =>
        dir.EnumerateAsync(query ?? new ComputerQuery(), CancellationToken.None).ToListAsync();

    [Fact]
    public async Task RootDnAndDomainController_AreCorpLocal()
    {
        var (_, dir, _) = Create();

        Assert.Equal("DC=corp,DC=local", await dir.GetRootDnAsync(CancellationToken.None));
        Assert.Equal("DC01.corp.local", dir.DomainController);
    }

    [Fact]
    public async Task GetRootDn_Cancelled_Throws()
    {
        var (_, dir, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.GetRootDnAsync(cts.Token));
    }

    [Fact]
    public async Task Enumerate_Default_ReturnsEnabledComputersOnly()
    {
        var (data, dir, _) = Create();

        var result = await All(dir);

        var enabled = data.GetComputers().Where(c => c.Enabled).ToList();
        Assert.True(enabled.Count < data.ComputerCount, "В тестовых данных должны быть отключённые компьютеры.");
        Assert.Equal(enabled.Select(c => c.Id).Order(), result.Select(c => c.Id).Order());
        Assert.All(result, c => Assert.True(c.Enabled));
        Assert.Equal(result.Count, result.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task Enumerate_IncludeDisabled_ReturnsAll()
    {
        var (data, dir, _) = Create();

        var result = await All(dir, new ComputerQuery(IncludeDisabled: true));

        Assert.Equal(data.ComputerCount, result.Count);
        Assert.Contains(result, c => !c.Enabled);
    }

    [Fact]
    public async Task Enumerate_ChangedSince_FiltersByWhenChanged()
    {
        var (data, dir, _) = Create();
        var since = TestSupport.Now.AddDays(-2);

        var result = await All(dir, new ComputerQuery(since, IncludeDisabled: true));

        var expected = data.GetComputers().Where(c => c.WhenChanged >= since).Select(c => c.Id).Order().ToList();
        Assert.NotEmpty(expected);
        Assert.True(expected.Count < data.ComputerCount);
        Assert.Equal(expected, result.Select(c => c.Id).Order());
        Assert.All(result, c => Assert.True(c.WhenChanged >= since));
    }

    [Fact]
    public async Task Enumerate_ChangedSince_CombinesWithEnabledFilter()
    {
        var (data, dir, _) = Create();
        var since = TestSupport.Now.AddDays(-30);

        var onlyEnabled = await All(dir, new ComputerQuery(since));
        var withDisabled = await All(dir, new ComputerQuery(since, IncludeDisabled: true));

        Assert.All(onlyEnabled, c => Assert.True(c.Enabled));
        Assert.Equal(
            data.GetComputers().Count(c => c.Enabled && c.WhenChanged >= since),
            onlyEnabled.Count);
        Assert.True(withDisabled.Count >= onlyEnabled.Count);
    }

    [Fact]
    public async Task Enumerate_ChangedSince_IsInclusiveAndAcceptsLocalTime()
    {
        var (data, dir, _) = Create();
        var edge = data.GetComputers().Where(c => c.Enabled).OrderByDescending(c => c.WhenChanged).Skip(3).First();

        var utc = await All(dir, new ComputerQuery(edge.WhenChanged));
        var local = await All(dir, new ComputerQuery(edge.WhenChanged!.Value.ToLocalTime()));

        Assert.Contains(utc, c => c.Id == edge.Id);
        Assert.Equal(utc.Select(c => c.Id).Order(), local.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Enumerate_ChangedSinceInFuture_ReturnsNothing()
    {
        var (_, dir, _) = Create();

        Assert.Empty(await All(dir, new ComputerQuery(TestSupport.Now.AddDays(1), IncludeDisabled: true)));
    }

    [Fact]
    public async Task Enumerate_IsOrderedByDistinguishedName()
    {
        var (_, dir, _) = Create();

        var dns = (await All(dir, new ComputerQuery(IncludeDisabled: true))).Select(c => c.DistinguishedName).ToList();

        Assert.Equal(dns.Order(StringComparer.OrdinalIgnoreCase), dns);
    }

    [Fact]
    public async Task Enumerate_ReturnsIsOnlineUnknown()
    {
        var (_, dir, _) = Create();

        // Статус «в сети» — работа проверки порта, а не каталога (как и в AD).
        Assert.All(await All(dir), c => Assert.Null(c.IsOnline));
    }

    [Fact]
    public async Task Enumerate_PausesBeforeEachPageOf500()
    {
        var data = TestSupport.Data(1200, 1);
        var dir = new DemoComputerDirectory(data, new DemoTimings { DirectoryPageDelay = TimeSpan.FromMilliseconds(80) });

        var watch = Stopwatch.StartNew();
        var result = await All(dir, new ComputerQuery(IncludeDisabled: true));
        watch.Stop();

        // 1200 записей = 3 страницы (500 + 500 + 200), перед каждой пауза.
        Assert.Equal(1200, result.Count);
        Assert.True(watch.ElapsedMilliseconds >= 150, $"Загрузка заняла {watch.ElapsedMilliseconds} мс: паузы между страницами не работают.");
        Assert.Equal(500, DemoComputerDirectory.PageSize);
    }

    [Fact]
    public async Task Enumerate_CancelledDuringPageDelay_Throws()
    {
        var data = TestSupport.Data();
        var dir = new DemoComputerDirectory(data, new DemoTimings { DirectoryPageDelay = TimeSpan.FromSeconds(30) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.EnumerateAsync(new ComputerQuery(), cts.Token).ToListAsync());

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Отмена должна прерывать паузу, а не ждать её конца.");
    }

    [Fact]
    public async Task Enumerate_CancelledMidPage_StopsImmediately()
    {
        var (_, dir, _) = Create();
        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in dir.EnumerateAsync(new ComputerQuery(), cts.Token))
            {
                if (++seen == 10) cts.Cancel();
            }
        });

        Assert.Equal(10, seen);
    }

    [Fact]
    public async Task Enumerate_AlreadyCancelled_Throws()
    {
        var (_, dir, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.EnumerateAsync(new ComputerQuery(), cts.Token).ToListAsync());
    }

    [Fact]
    public async Task Enumerate_TakesSnapshot_ChangesDuringLoadAreNotMixedIn()
    {
        var (data, dir, _) = Create();
        var victim = data.GetComputers().First(c => c.Enabled);
        var seen = new List<ComputerInfo>();

        await foreach (var c in dir.EnumerateAsync(new ComputerQuery(), CancellationToken.None))
        {
            if (seen.Count == 0) dir.Remove(victim.Id);
            seen.Add(c);
        }

        Assert.Equal(data.GetComputers().Count(x => x.Enabled) + 1, seen.Count);
    }

    // ---------- подразделения ----------

    [Fact]
    public async Task GetOuChildren_Root_ReturnsTopLevelOus()
    {
        var (_, dir, _) = Create();

        var nodes = await dir.GetOuChildrenAsync(DemoData.RootDn, CancellationToken.None);

        Assert.Equal(
            new[] { "Офис", "Продажи", "Серверы", "Склад", "Филиал-Север" }.Order(StringComparer.InvariantCultureIgnoreCase),
            nodes.Select(n => n.Name));
        Assert.True(nodes.Single(n => n.Name == "Офис").HasChildren);
        Assert.True(nodes.Single(n => n.Name == "Продажи").HasChildren);
        Assert.False(nodes.Single(n => n.Name == "Склад").HasChildren);
        Assert.False(nodes.Single(n => n.Name == "Серверы").HasChildren);
        Assert.False(nodes.Single(n => n.Name == "Филиал-Север").HasChildren);
        Assert.All(nodes, n => Assert.Equal($"OU={n.Name},{DemoData.RootDn}", n.DistinguishedName));
    }

    [Fact]
    public async Task GetOuChildren_Office_ReturnsFourDepartments()
    {
        var (_, dir, _) = Create();

        var nodes = await dir.GetOuChildrenAsync(Office, CancellationToken.None);

        Assert.Equal(
            new[] { "Бухгалтерия", "Отдел кадров", "ИТ", "Юристы" }.Order(StringComparer.InvariantCultureIgnoreCase),
            nodes.Select(n => n.Name));
        Assert.All(nodes, n => Assert.False(n.HasChildren));
        Assert.All(nodes, n => Assert.True(Dn.IsDirectChildOf(n.DistinguishedName, Office)));
    }

    [Fact]
    public async Task GetOuChildren_Sales_ReturnsCities()
    {
        var (_, dir, _) = Create();

        var nodes = await dir.GetOuChildrenAsync("OU=Продажи,DC=corp,DC=local", CancellationToken.None);

        Assert.Equal(new[] { "Казань", "Москва" }, nodes.Select(n => n.Name));
    }

    [Fact]
    public async Task GetOuChildren_LeafAndUnknown_ReturnEmpty()
    {
        var (_, dir, _) = Create();

        Assert.Empty(await dir.GetOuChildrenAsync(Warehouse, CancellationToken.None));
        Assert.Empty(await dir.GetOuChildrenAsync("OU=Нет такого,DC=corp,DC=local", CancellationToken.None));
    }

    [Fact]
    public async Task GetOuChildren_ToleratesCaseAndSpacesInDn()
    {
        var (_, dir, _) = Create();

        var nodes = await dir.GetOuChildrenAsync("ou=офис, dc=CORP , dc=local", CancellationToken.None);

        Assert.Equal(4, nodes.Count);
    }

    [Fact]
    public async Task GetOuChildren_EveryComputerLivesInAnOuReachableFromRoot()
    {
        var (data, dir, _) = Create();
        var reachable = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(DemoData.RootDn);

        while (queue.Count > 0)
        {
            foreach (var node in await dir.GetOuChildrenAsync(queue.Dequeue(), CancellationToken.None))
            {
                reachable.Add(Dn.Normalize(node.DistinguishedName));
                if (node.HasChildren) queue.Enqueue(node.DistinguishedName);
            }
        }

        Assert.All(data.GetComputers(), c => Assert.Contains(Dn.Normalize(c.ParentDn), reachable));
    }

    // ---------- изменения каталога ----------

    [Fact]
    public async Task Rename_ChangesNameDnsNameAndDn_KeepsGuid_AndUpdatesWhenChanged()
    {
        var (data, dir, clock) = Create();
        var old = data.GetComputers().First(c => c.Enabled && c.DnsName is not null);
        clock.Value = TestSupport.Now.AddHours(2);

        var renamed = dir.Rename(old.Id, "PC-NEW-001");

        Assert.Equal(old.Id, renamed.Id);
        Assert.Equal("PC-NEW-001", renamed.Name);
        Assert.Equal("PC-NEW-001.corp.local", renamed.DnsName);
        Assert.Equal($"CN=PC-NEW-001,{old.ParentDn}", renamed.DistinguishedName);
        Assert.Equal(old.Description, renamed.Description);
        Assert.Equal(clock.Value, renamed.WhenChanged);
        Assert.True(renamed.WhenChanged > old.WhenChanged);

        Assert.Equal(renamed, data.FindComputer(old.Id));
        Assert.Equal(old.Id, data.FindComputerByHost("pc-new-001")!.Id);
        Assert.Null(data.FindComputerByHost(old.Name));

        var listed = (await All(dir, new ComputerQuery(IncludeDisabled: true))).Single(c => c.Id == old.Id);
        Assert.Equal("PC-NEW-001", listed.Name);
    }

    [Fact]
    public void Rename_ComputerWithoutDnsName_StaysWithoutDnsName()
    {
        var (data, dir, _) = Create();
        var old = data.GetComputers().First(c => c.DnsName is null);

        Assert.Null(dir.Rename(old.Id, "PC-NEW-002").DnsName);
    }

    [Fact]
    public void Rename_Invalid_Throws()
    {
        var (data, dir, _) = Create();
        var computer = data.GetComputers()[0];
        var other = data.GetComputers()[1];

        var ex = Assert.Throws<OperationFailedException>(() => dir.Rename(computer.Id, "-bad name"));
        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        Assert.Throws<InvalidOperationException>(() => dir.Rename(computer.Id, other.Name));
        Assert.Throws<ArgumentException>(() => dir.Rename(computer.Id, "pc.with.dots"));
        Assert.Throws<KeyNotFoundException>(() => dir.Rename(Guid.NewGuid(), "PC-NEW-003"));
    }

    [Fact]
    public async Task MoveTo_ChangesDn_KeepsName_AndUpdatesWhenChanged()
    {
        var (data, dir, clock) = Create();
        var old = data.GetComputers().First(c => c.Enabled && !Dn.AreEqual(c.ParentDn, Warehouse));
        clock.Value = TestSupport.Now.AddHours(3);

        var moved = dir.MoveTo(old.Id, Warehouse);

        Assert.Equal(old.Id, moved.Id);
        Assert.Equal(old.Name, moved.Name);
        Assert.Equal($"CN={old.Name},{Warehouse}", moved.DistinguishedName);
        Assert.Equal(Warehouse, moved.ParentDn);
        Assert.Equal(clock.Value, moved.WhenChanged);
        Assert.True(moved.WhenChanged > old.WhenChanged);

        Assert.Equal(moved.DistinguishedName, (await All(dir)).Single(c => c.Id == old.Id).DistinguishedName);
    }

    [Fact]
    public void MoveTo_UnknownOu_Throws()
    {
        var (data, dir, _) = Create();
        var computer = data.GetComputers()[0];

        Assert.Throws<ArgumentException>(() => dir.MoveTo(computer.Id, "OU=Нет такого,DC=corp,DC=local"));
        Assert.Throws<KeyNotFoundException>(() => dir.MoveTo(Guid.NewGuid(), Warehouse));
    }

    [Fact]
    public void RepeatedChangesWithFrozenClock_StillGiveIncreasingWhenChanged()
    {
        var (data, dir, _) = Create();
        var id = data.GetComputers()[0].Id;

        var first = dir.Rename(id, "PC-NEW-010").WhenChanged;
        var second = dir.Rename(id, "PC-NEW-011").WhenChanged;
        var third = dir.MoveTo(id, Warehouse).WhenChanged;

        Assert.True(first < second);
        Assert.True(second < third);
    }

    [Fact]
    public async Task PartialRefresh_SeesRenameMoveAndAdd()
    {
        var (data, dir, clock) = Create();
        var watermark = clock.Value;
        clock.Value = watermark.AddMinutes(5);

        var computers = data.GetComputers().Where(c => c.Enabled).Take(2).ToList();
        dir.Rename(computers[0].Id, "PC-NEW-020");
        dir.MoveTo(computers[1].Id, Warehouse);
        var added = dir.Add("PC-NEW-021", Office, "Новый компьютер");

        var changed = await All(dir, new ComputerQuery(watermark));

        Assert.Equal(
            new[] { computers[0].Id, computers[1].Id, added.Id }.Order(),
            changed.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Add_CreatesNewComputerWithFreshGuidAndWhenChanged()
    {
        var (data, dir, clock) = Create();
        var before = data.ComputerCount;
        clock.Value = TestSupport.Now.AddHours(4);

        var added = dir.Add("pc-new-030", Warehouse, "Склад, тест", "Windows 10 Pro", online: true);

        Assert.Equal(before + 1, data.ComputerCount);
        Assert.NotEqual(Guid.Empty, added.Id);
        Assert.Equal(data.ComputerCount, data.GetComputers().Select(c => c.Id).Distinct().Count());
        Assert.Equal("pc-new-030", added.Name);
        Assert.Equal("pc-new-030.corp.local", added.DnsName);
        Assert.Equal($"CN=pc-new-030,{Warehouse}", added.DistinguishedName);
        Assert.Equal("Windows 10 Pro", added.OperatingSystem);
        Assert.Equal(clock.Value, added.WhenChanged);
        Assert.True(added.Enabled);
        Assert.True(data.IsOnline(added.Id));

        Assert.Contains(await All(dir), c => c.Id == added.Id);
    }

    [Fact]
    public void Add_Invalid_Throws()
    {
        var (data, dir, _) = Create();
        var existing = data.GetComputers()[0];

        Assert.Throws<InvalidOperationException>(() => dir.Add(existing.Name.ToLowerInvariant(), Warehouse));
        Assert.Throws<ArgumentException>(() => dir.Add("PC-NEW-031", "OU=Нет такого,DC=corp,DC=local"));
        Assert.Throws<OperationFailedException>(() => dir.Add("bad name", Warehouse));
    }

    [Fact]
    public void Add_Server_GetsServerOperatingSystemVersion_AndNoLoginScreen()
    {
        var (data, dir, _) = Create();

        var server = dir.Add("SRV-NEW-01", "OU=Серверы,DC=corp,DC=local", "Тестовый сервер", "Windows Server 2022 Standard");

        Assert.Equal("10.0 (20348)", server.OperatingSystemVersion);
        Assert.Empty(data.GetSessions(server.Id));
    }

    [Fact]
    public async Task Remove_DeletesComputer_PartialRefreshDoesNotSeeIt()
    {
        var (data, dir, clock) = Create();
        var victim = data.GetComputers().First(c => c.Enabled);
        var watermark = clock.Value;
        clock.Value = watermark.AddMinutes(5);

        Assert.True(dir.Remove(victim.Id));
        Assert.False(dir.Remove(victim.Id));

        Assert.Null(data.FindComputer(victim.Id));
        Assert.Null(data.FindComputerByHost(victim.Name));
        Assert.DoesNotContain(await All(dir, new ComputerQuery(IncludeDisabled: true)), c => c.Id == victim.Id);

        // Как в AD: удаление частичное обновление не видит.
        Assert.Empty(await All(dir, new ComputerQuery(watermark, IncludeDisabled: true)));
    }

    [Fact]
    public async Task SetEnabled_False_HidesComputerFromDefaultQuery()
    {
        var (data, dir, clock) = Create();
        var computer = data.GetComputers().First(c => c.Enabled && data.IsOnline(c.Id));
        clock.Value = TestSupport.Now.AddHours(5);

        var disabled = dir.SetEnabled(computer.Id, false);

        Assert.False(disabled.Enabled);
        Assert.Equal(clock.Value, disabled.WhenChanged);
        Assert.False(data.IsOnline(computer.Id));
        Assert.DoesNotContain(await All(dir), c => c.Id == computer.Id);
        Assert.Contains(await All(dir, new ComputerQuery(IncludeDisabled: true)), c => c.Id == computer.Id);

        Assert.True(dir.SetEnabled(computer.Id, true).Enabled);
        Assert.Contains(await All(dir), c => c.Id == computer.Id);
    }

    [Fact]
    public async Task SwitchDomainController_ChangesNameAndBumpsWhenChanged()
    {
        var (data, dir, clock) = Create();
        var watermark = clock.Value;
        clock.Value = watermark.AddMinutes(10);

        dir.SwitchDomainController("DC02.corp.local");

        Assert.Equal("DC02.corp.local", dir.DomainController);
        Assert.Equal("DC02.corp.local", data.DomainController);

        // whenChanged не реплицируется: после смены контроллера все отметки новые, и частичное обновление вернёт всё.
        var all = await All(dir, new ComputerQuery(watermark, IncludeDisabled: true));
        Assert.Equal(data.ComputerCount, all.Count);
        Assert.All(all, c => Assert.True(c.WhenChanged > watermark));
    }

    [Fact]
    public async Task SwitchDomainController_WithoutBump_KeepsWhenChanged()
    {
        var (data, dir, clock) = Create();
        var before = data.GetComputers().ToDictionary(c => c.Id, c => c.WhenChanged);
        clock.Value = clock.Value.AddMinutes(10);

        dir.SwitchDomainController("DC02.corp.local", bumpWhenChanged: false);

        Assert.Equal("DC02.corp.local", dir.DomainController);
        Assert.All(data.GetComputers(), c => Assert.Equal(before[c.Id], c.WhenChanged));
        Assert.Empty(await All(dir, new ComputerQuery(clock.Value, IncludeDisabled: true)));
    }

    [Fact]
    public void SwitchDomainController_EmptyName_Throws()
    {
        var (_, dir, _) = Create();

        Assert.ThrowsAny<ArgumentException>(() => dir.SwitchDomainController(" "));
    }

    [Fact]
    public async Task ConcurrentChangesAndEnumeration_DoNotCorruptCatalog()
    {
        var (data, dir, _) = Create(500);
        var ids = data.GetComputers().Select(c => c.Id).ToArray();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writers = Task.Run(() => Parallel.For(0, 200, i =>
        {
            try
            {
                dir.Rename(ids[i], $"PC-RN-{i:D4}");
                dir.MoveTo(ids[i], i % 2 == 0 ? Warehouse : Office);
            }
            catch (Exception e)
            {
                errors.Add(e);
            }
        }));

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                for (var k = 0; k < 5; k++) await All(dir, new ComputerQuery(IncludeDisabled: true));
            }
            catch (Exception e)
            {
                errors.Add(e);
            }
        })).ToArray();

        await writers;
        await Task.WhenAll(readers);

        Assert.Empty(errors);
        Assert.Equal(500, (await All(dir, new ComputerQuery(IncludeDisabled: true))).Count);
    }
}
