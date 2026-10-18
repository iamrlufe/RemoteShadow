using System.Text;
using System.Text.Json;
using RemoteShadow.Core.Models;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

public class ComputerIndexCacheTests
{
    private static readonly DateTime Start = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static string[] Names(IEnumerable<ComputerInfo> computers) => computers.Select(c => c.Name).ToArray();

    private static async Task<IndexFixture> SavedCacheAsync()
    {
        var fx = new IndexFixture().Seed(
            Pc("PC-1", AccountingOu, id: 1, description: "кабинет 5") with { OperatingSystemVersion = "10.0 (22631)" },
            Pc("PC-2", WarehouseOu, id: 2));
        await fx.FullLoadAsync();
        fx.Index.SetOnline(IdOf(1), true);
        return fx;
    }

    /// <summary>Каталог, который держит обновление, пока тест не откроет задвижку: видно, что показывает индекс из кэша.</summary>
    private static (FakeComputerDirectory Ad, TaskCompletionSource Gate) HeldDirectory()
    {
        var gate = new TaskCompletionSource();
        return (new FakeComputerDirectory { Gate = gate.Task }, gate);
    }

    // ----- запись кэша --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cache_is_written_after_successful_sync_with_domain_controller_and_times()
    {
        using var fx = await SavedCacheAsync();

        Assert.True(File.Exists(fx.CacheFile));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(fx.CacheFile));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("corp.local", root.GetProperty("domain").GetString());
        Assert.Equal("dc1.corp.local", root.GetProperty("domainController").GetString());
        Assert.Equal(Start, root.GetProperty("lastSyncUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(Start, root.GetProperty("lastFullSyncUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(2, root.GetProperty("computers").GetArrayLength());
    }

    [Fact]
    public async Task Cache_has_no_online_status_and_no_derived_properties()
    {
        using var fx = await SavedCacheAsync();

        var text = await File.ReadAllTextAsync(fx.CacheFile);

        Assert.DoesNotContain("online", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hostName", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parentDn", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("кабинет 5", text); // кириллица пишется как есть
    }

    [Fact]
    public async Task Cache_file_is_written_atomically_without_temp_leftovers()
    {
        using var fx = await SavedCacheAsync();

        Assert.Equal(new[] { fx.CacheFile }, Directory.GetFiles(fx.Temp.Paths.CacheDir));
    }

    [Fact]
    public async Task Failed_refresh_does_not_create_cache()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        fx.Ad.FailWith = new InvalidOperationException("сбой");

        await fx.FullLoadAsync();

        Assert.False(File.Exists(fx.CacheFile));
    }

    [Fact]
    public async Task Failed_refresh_keeps_previous_cache_intact()
    {
        using var fx = await SavedCacheAsync();
        var before = await File.ReadAllBytesAsync(fx.CacheFile);
        fx.Ad.FailWith = new InvalidOperationException("сбой");

        await fx.FullLoadAsync();

        Assert.Equal(before, await File.ReadAllBytesAsync(fx.CacheFile));
    }

    [Fact]
    public async Task Partial_refresh_with_changes_rewrites_cache_and_without_changes_leaves_it()
    {
        using var fx = await SavedCacheAsync();
        var original = await File.ReadAllBytesAsync(fx.CacheFile);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, CancellationToken.None);
        Assert.Equal(original, await File.ReadAllBytesAsync(fx.CacheFile)); // нечего сохранять

        fx.Ad.Computers.Add(Pc("PC-3", id: 3, whenChanged: fx.Time.GetUtcNow().UtcDateTime));
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Index.RefreshAsync(full: false, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(fx.CacheFile));
        Assert.Equal(3, document.RootElement.GetProperty("computers").GetArrayLength());
    }

    [Fact]
    public async Task Unwritable_cache_folder_does_not_break_refresh()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        // На месте папки кэша лежит файл: создать в нём ничего нельзя.
        Directory.CreateDirectory(fx.Temp.Paths.DataDir);
        await File.WriteAllTextAsync(fx.Temp.Paths.CacheDir, "x");

        await fx.FullLoadAsync();

        Assert.Single(fx.Index.All);
        Assert.Null(fx.Index.LastError);
    }

    // ----- чтение кэша --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_shows_cache_at_once_and_refreshes_in_background_without_blocking()
    {
        using var fx = await SavedCacheAsync();
        var (ad, gate) = HeldDirectory();
        ad.Computers.Add(Pc("PC-1", AccountingOu, id: 1, description: "кабинет 5", whenChanged: Start.AddMinutes(1)));
        ad.Computers.Add(Pc("PC-2", WarehouseOu, id: 2));
        ad.Computers.Add(Pc("PC-NEW", WarehouseOu, id: 3, whenChanged: Start.AddMinutes(2)));
        var restarted = fx.Create(ad);
        var changes = 0;
        restarted.Changed += (_, _) => Interlocked.Increment(ref changes);

        await restarted.InitializeAsync(CancellationToken.None).WithTimeout(); // не ждёт AD

        Assert.Equal(new[] { "PC-1", "PC-2" }, Names(restarted.All));
        Assert.Equal(Start, restarted.LastSyncUtc);
        Assert.True(changes >= 1);
        Assert.Equal("кабинет 5", restarted.Find(IdOf(1))!.Description);
        Assert.Null(restarted.Find(IdOf(1))!.IsOnline); // «в сети» из кэша не берётся
        Assert.Equal(new[] { "PC-1" }, Names(restarted.Search("кабинет")));
        Assert.Equal(new[] { "PC-2" }, Names(restarted.GetByOu(WarehouseOu, false)));

        await ad.Entered.Task.WithTimeout();
        await Wait.UntilAsync(() => restarted.IsLoading, what: "фоновое обновление запущено");
        Assert.Equal(2, restarted.All.Count); // пока AD молчит, показывается кэш

        gate.SetResult();
        await restarted.BackgroundRefresh.WithTimeout();

        Assert.Equal(new[] { "PC-1", "PC-2", "PC-NEW" }, Names(restarted.All));
        Assert.False(restarted.IsLoading);
    }

    [Fact]
    public async Task Background_refresh_after_restart_is_partial_when_cache_is_fresh_and_controller_is_the_same()
    {
        using var fx = await SavedCacheAsync();
        var (ad, gate) = HeldDirectory();
        gate.SetResult();
        fx.Time.Advance(TimeSpan.FromMinutes(10));
        var restarted = fx.Create(ad);

        await restarted.InitializeAsync(CancellationToken.None);
        await restarted.BackgroundRefresh.WithTimeout();

        Assert.Equal(Start.AddMinutes(-5), ad.Queries[0].ChangedSince); // от метки в кэше, а не от «сейчас»
    }

    [Fact]
    public async Task Background_refresh_after_restart_is_full_when_controller_differs_from_cache()
    {
        using var fx = await SavedCacheAsync();
        var (ad, gate) = HeldDirectory();
        gate.SetResult();
        ad.DomainController = "dc7.corp.local";
        var restarted = fx.Create(ad);

        await restarted.InitializeAsync(CancellationToken.None);
        await restarted.BackgroundRefresh.WithTimeout();

        Assert.Null(ad.Queries[0].ChangedSince);
        Assert.Empty(restarted.All); // в новом каталоге пусто, а полная загрузка удаляет пропавших
    }

    [Fact]
    public async Task Background_refresh_after_restart_is_full_when_cache_is_older_than_an_hour()
    {
        using var fx = await SavedCacheAsync();
        var (ad, gate) = HeldDirectory();
        gate.SetResult();
        fx.Time.Advance(TimeSpan.FromMinutes(61));
        var restarted = fx.Create(ad);

        await restarted.InitializeAsync(CancellationToken.None);
        await restarted.BackgroundRefresh.WithTimeout();

        Assert.Null(ad.Queries[0].ChangedSince);
    }

    [Fact]
    public async Task Without_cache_first_refresh_is_full()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));

        await fx.Index.InitializeAsync(CancellationToken.None);
        await fx.Index.BackgroundRefresh.WithTimeout();

        Assert.Null(fx.Ad.Queries[0].ChangedSince);
        Assert.Single(fx.Index.All);
        Assert.True(File.Exists(fx.CacheFile));
    }

    [Theory]
    [InlineData("{ это не json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\":1,\"computers\":\"не список\"}")]
    [InlineData("{\"version\":999,\"domain\":\"corp.local\",\"computers\":[{\"id\":\"00000001-0000-0000-0000-000000000000\",\"name\":\"PC-1\",\"distinguishedName\":\"CN=PC-1,DC=corp,DC=local\"}]}")]
    [InlineData("{\"version\":1,\"domain\":\"other.local\",\"computers\":[{\"id\":\"00000001-0000-0000-0000-000000000000\",\"name\":\"PC-1\",\"distinguishedName\":\"CN=PC-1,DC=corp,DC=local\"}]}")]
    public async Task Broken_or_foreign_cache_is_ignored(string content)
    {
        using var fx = new IndexFixture();
        Directory.CreateDirectory(fx.Temp.Paths.CacheDir);
        await File.WriteAllTextAsync(fx.CacheFile, content, Encoding.UTF8);
        var (ad, gate) = HeldDirectory();
        var index = fx.Create(ad);

        await index.InitializeAsync(CancellationToken.None).WithTimeout();

        Assert.Empty(index.All);
        Assert.Null(index.LastSyncUtc);
        gate.SetResult();
        await index.BackgroundRefresh.WithTimeout();
    }

    [Fact]
    public async Task Cache_file_with_utf8_bom_is_still_read()
    {
        using var fx = await SavedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(fx.CacheFile);
        await File.WriteAllBytesAsync(fx.CacheFile, [0xEF, 0xBB, 0xBF, .. bytes]);
        var (ad, gate) = HeldDirectory();
        var restarted = fx.Create(ad);

        await restarted.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, restarted.All.Count);
        gate.SetResult();
        await restarted.BackgroundRefresh.WithTimeout();
    }

    [Fact]
    public async Task Truncated_cache_file_is_ignored_and_replaced_after_first_sync()
    {
        using var fx = await SavedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(fx.CacheFile);
        await File.WriteAllBytesAsync(fx.CacheFile, bytes[..(bytes.Length / 2)]);
        var restarted = fx.Create();

        await restarted.InitializeAsync(CancellationToken.None);
        await restarted.BackgroundRefresh.WithTimeout();

        Assert.Equal(2, restarted.All.Count);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(fx.CacheFile));
        Assert.Equal(2, document.RootElement.GetProperty("computers").GetArrayLength());
    }

    [Fact]
    public async Task Cache_records_without_name_id_or_dn_are_skipped_and_duplicates_collapse()
    {
        using var fx = new IndexFixture();
        Directory.CreateDirectory(fx.Temp.Paths.CacheDir);
        const string json = """
        {
          "version": 1, "domain": "corp.local", "lastSyncUtc": "2026-01-15T10:00:00Z", "lastFullSyncUtc": "2026-01-15T10:00:00Z",
          "computers": [
            { "id": "00000001-0000-0000-0000-000000000000", "name": "GOOD", "distinguishedName": "CN=GOOD,OU=A,DC=corp,DC=local" },
            { "id": "00000002-0000-0000-0000-000000000000", "name": "", "distinguishedName": "CN=X,OU=A,DC=corp,DC=local" },
            { "id": "00000000-0000-0000-0000-000000000000", "name": "NOID", "distinguishedName": "CN=NOID,OU=A,DC=corp,DC=local" },
            { "id": "00000003-0000-0000-0000-000000000000", "name": "NODN", "distinguishedName": "" },
            { "id": "00000004-0000-0000-0000-000000000000", "name": "DUP", "distinguishedName": "CN=DUP,OU=A,DC=corp,DC=local" },
            { "id": "00000004-0000-0000-0000-000000000000", "name": "DUP2", "distinguishedName": "CN=DUP2,OU=A,DC=corp,DC=local" }
          ]
        }
        """;
        await File.WriteAllTextAsync(fx.CacheFile, json, new UTF8Encoding(false));
        var (ad, gate) = HeldDirectory();
        var index = fx.Create(ad);

        await index.InitializeAsync(CancellationToken.None);

        Assert.Equal(new[] { "DUP2", "GOOD" }, Names(index.All));
        gate.SetResult();
        await index.BackgroundRefresh.WithTimeout();
    }

    [Fact]
    public async Task Second_initialize_does_not_replace_data_with_older_cache()
    {
        using var fx = await SavedCacheAsync();
        var olderCache = await File.ReadAllBytesAsync(fx.CacheFile); // два компьютера
        fx.Ad.Computers.Add(Pc("PC-3", id: 3));
        await fx.FullLoadAsync();
        Assert.Equal(3, fx.Index.All.Count);
        await File.WriteAllBytesAsync(fx.CacheFile, olderCache);
        var gate = new TaskCompletionSource();
        fx.Ad.Gate = gate.Task; // обновление в фоне не успеет «исправить» результат

        await fx.Index.InitializeAsync(CancellationToken.None);

        Assert.Equal(3, fx.Index.All.Count);
        gate.SetResult();
        await fx.Index.BackgroundRefresh.WithTimeout();
        Assert.Equal(3, fx.Index.All.Count);
    }

    [Fact]
    public async Task Initialize_after_clear_loads_cache_again()
    {
        using var fx = await SavedCacheAsync();
        var (ad, gate) = HeldDirectory();
        var restarted = fx.Create(ad);
        await restarted.InitializeAsync(CancellationToken.None);
        restarted.Clear();
        Assert.Empty(restarted.All);

        await restarted.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, restarted.All.Count);
        gate.SetResult();
        await restarted.BackgroundRefresh.WithTimeout();
    }

    [Fact]
    public async Task Initialize_cancelled_while_reading_cache_throws_and_starts_nothing()
    {
        using var fx = await SavedCacheAsync();
        var (ad, gate) = HeldDirectory();
        var restarted = fx.Create(ad);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.InitializeAsync(cts.Token));

        Assert.Equal(0, ad.EnumerateCalls);
        gate.SetResult();
    }

    [Fact]
    public async Task Cache_without_connection_is_found_by_domain_from_directory_root()
    {
        using var fx = await SavedCacheAsync();
        var (ad, gate) = HeldDirectory();
        fx.Connection.Set(null);
        var restarted = fx.Create(ad);

        await restarted.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, restarted.All.Count);
        gate.SetResult();
        await restarted.BackgroundRefresh.WithTimeout();
    }

    // ----- имя файла кэша -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("corp.local", "corp.local.json")]
    [InlineData("CORP.Local", "corp.local.json")]
    [InlineData("  corp.local  ", "corp.local.json")]
    [InlineData("корп.local", "корп.local.json")]
    [InlineData("a/b\\c:d", "a_b_c_d.json")]
    [InlineData("..\\..\\windows", "_.._windows.json")]
    [InlineData("con", "_con.json")]
    [InlineData("NUL.corp.local", "_nul.corp.local.json")]
    [InlineData("com1.local", "_com1.local.json")]
    [InlineData("lpt9", "_lpt9.json")]
    [InlineData("", "domain.json")]
    [InlineData("...", "domain.json")]
    public void Cache_file_name_is_safe_for_the_file_system(string domain, string expected) =>
        Assert.Equal(expected, ComputerCacheFile.FileNameFor(domain));

    [Fact]
    public void Cache_file_name_never_contains_path_characters_and_is_not_too_long()
    {
        var evil = new string('x', 300) + @"\..\*?""<>|:";

        var name = ComputerCacheFile.FileNameFor(evil);

        Assert.True(name.Length <= 105);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    // ----- таймеры ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Timers_start_partial_refresh_every_5_minutes_and_full_refresh_after_an_hour()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.Index.InitializeAsync(CancellationToken.None);
        await fx.Index.BackgroundRefresh.WithTimeout();
        Assert.Equal(1, fx.Ad.EnumerateCalls);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await Wait.UntilAsync(() => fx.Ad.EnumerateCalls == 2, what: "частичное обновление по таймеру");
        await Wait.UntilAsync(() => !fx.Index.IsLoading);
        Assert.NotNull(fx.Ad.Queries[1].ChangedSince);

        fx.Time.Advance(TimeSpan.FromMinutes(55)); // сработают и частичные таймеры, и часовой
        // Задачи от всех сработавших таймеров идут параллельно: какая-то из них обязана оказаться полной.
        await Wait.UntilAsync(
            () => fx.Ad.Queries.Skip(2).Any(q => q.ChangedSince is null),
            what: "полное обновление по часовому таймеру");
        await Wait.UntilAsync(() => !fx.Index.IsLoading);
    }

    [Fact]
    public async Task Timer_intervals_come_from_settings()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        fx.Settings.Settings.PartialRefreshMinutes = 2;
        await fx.Index.InitializeAsync(CancellationToken.None);
        await fx.Index.BackgroundRefresh.WithTimeout();

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);
        Assert.Equal(1, fx.Ad.EnumerateCalls);

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => fx.Ad.EnumerateCalls == 2);
    }

    [Fact]
    public async Task Timers_do_nothing_while_signed_out()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.Index.InitializeAsync(CancellationToken.None);
        await fx.Index.BackgroundRefresh.WithTimeout();
        fx.Connection.Set(null);

        fx.Time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(150);

        Assert.Equal(1, fx.Ad.EnumerateCalls);
        Assert.Null(fx.Index.LastError);
    }

    [Fact]
    public async Task Dispose_stops_timers()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.Index.InitializeAsync(CancellationToken.None);
        await fx.Index.BackgroundRefresh.WithTimeout();

        fx.Index.Dispose();
        fx.Time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(150);

        Assert.Equal(1, fx.Ad.EnumerateCalls);
    }

    [Fact]
    public async Task Repeated_initialize_does_not_multiply_timers()
    {
        using var fx = new IndexFixture().Seed(Pc("PC-1", id: 1));
        await fx.Index.InitializeAsync(CancellationToken.None);
        var first = fx.Index.BackgroundRefresh;
        await fx.Index.InitializeAsync(CancellationToken.None);
        await Task.WhenAll(first, fx.Index.BackgroundRefresh).WithTimeout();

        Assert.Equal(2, fx.Time.ActiveTimers); // частичный и полный, а не по паре от каждого вызова
    }
}
