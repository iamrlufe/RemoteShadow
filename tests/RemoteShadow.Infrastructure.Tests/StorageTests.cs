using System.Text;
using System.Text.Json;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Infrastructure.Tests;

public class AppPathsTests
{
    [Fact]
    public void Default_paths_follow_windows_layout()
    {
        var paths = new AppPaths();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(appData, "RemoteShadow"), paths.DataDir);
        Assert.Equal(Path.Combine(localAppData, "RemoteShadow", "cache"), paths.CacheDir);
        Assert.Equal(Path.Combine(paths.DataDir, "audit"), paths.AuditDir);
        Assert.Equal(Path.Combine(paths.DataDir, "settings.json"), paths.SettingsFile);
    }

    [Fact]
    public void Root_override_keeps_everything_inside_the_root()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);

        foreach (var path in new[] { paths.DataDir, paths.CacheDir, paths.AuditDir, paths.SettingsFile })
        {
            Assert.StartsWith(temp.Path, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Constructor_does_not_create_directories()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "not-created");

        _ = new AppPaths(root);

        Assert.False(Directory.Exists(root));
    }
}

public class JsonSettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults_and_is_not_created_by_load()
    {
        using var temp = new TempDir();
        var store = new JsonSettingsStore(temp.Paths);

        var settings = store.Load();

        Assert.True(settings.UseCurrentUser);
        Assert.Equal(8, settings.BulkParallelism);
        Assert.False(File.Exists(temp.Paths.SettingsFile));
    }

    [Fact]
    public void Save_then_load_in_new_instance_returns_same_values()
    {
        using var temp = new TempDir();
        var favorite = Guid.NewGuid();
        var store = new JsonSettingsStore(temp.Paths);

        var settings = store.Load();
        settings.LastDomain = "corp.local";
        settings.Theme = "Dark";
        settings.BulkParallelism = 3;
        settings.AuditCentralFolder = @"\\srv\audit$\RemoteShadow";
        settings.FavoriteComputers.Add(favorite);
        store.Save(settings);

        var loaded = new JsonSettingsStore(temp.Paths).Load();

        Assert.Equal("corp.local", loaded.LastDomain);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal(3, loaded.BulkParallelism);
        Assert.Equal(@"\\srv\audit$\RemoteShadow", loaded.AuditCentralFolder);
        Assert.Equal(new[] { favorite }, loaded.FavoriteComputers);
    }

    [Fact]
    public void File_is_indented_camel_case_json()
    {
        using var temp = new TempDir();
        var store = new JsonSettingsStore(temp.Paths);

        store.Save(new AppSettings { LastDomain = "corp.local" });

        var text = File.ReadAllText(temp.Paths.SettingsFile);
        Assert.Contains("\"lastDomain\": \"corp.local\"", text);
        Assert.Contains("\"useCurrentUser\": true", text);
        Assert.Contains(Environment.NewLine, text);
    }

    [Fact]
    public void Load_returns_independent_copies_until_saved()
    {
        using var temp = new TempDir();
        var store = new JsonSettingsStore(temp.Paths);

        var first = store.Load();
        first.Theme = "Dark";
        first.FavoriteComputers.Add(Guid.NewGuid());

        var second = store.Load();
        Assert.Equal("System", second.Theme);
        Assert.Empty(second.FavoriteComputers);

        store.Save(first);
        Assert.Equal("Dark", store.Load().Theme);
    }

    [Fact]
    public void Loaded_settings_are_cached_in_memory()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        File.WriteAllText(temp.Paths.SettingsFile, "{ \"theme\": \"Light\" }");
        var store = new JsonSettingsStore(temp.Paths);

        Assert.Equal("Light", store.Load().Theme);

        File.WriteAllText(temp.Paths.SettingsFile, "{ \"theme\": \"Dark\" }");
        Assert.Equal("Light", store.Load().Theme);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    public void Corrupt_file_is_renamed_to_bad_and_defaults_are_returned(string content)
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        File.WriteAllText(temp.Paths.SettingsFile, content);
        var store = new JsonSettingsStore(temp.Paths);

        var settings = store.Load();

        Assert.True(settings.UseCurrentUser);
        Assert.False(File.Exists(temp.Paths.SettingsFile));
        Assert.Equal(content, File.ReadAllText(temp.Paths.SettingsFile + ".bad"));
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void Corrupt_file_is_replaced_by_the_next_save_and_bad_copy_is_kept()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        File.WriteAllText(temp.Paths.SettingsFile, "{ broken");
        var store = new JsonSettingsStore(temp.Paths);

        var settings = store.Load();
        settings.Theme = "Dark";
        store.Save(settings);

        Assert.Equal("Dark", new JsonSettingsStore(temp.Paths).Load().Theme);
        Assert.Equal("{ broken", File.ReadAllText(temp.Paths.SettingsFile + ".bad"));
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Null_lists_in_hand_edited_file_become_empty_lists()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        File.WriteAllText(temp.Paths.SettingsFile, "{ \"favoriteComputers\": null, \"recentComputers\": null }");

        var settings = new JsonSettingsStore(temp.Paths).Load();

        Assert.NotNull(settings.FavoriteComputers);
        Assert.NotNull(settings.RecentComputers);
    }

    [Fact]
    public void Save_leaves_no_temporary_files_and_always_produces_valid_json()
    {
        using var temp = new TempDir();
        var store = new JsonSettingsStore(temp.Paths);

        Parallel.For(0, 40, i =>
        {
            var settings = store.Load();
            settings.BulkParallelism = i + 1;
            settings.LastUserName = new string('x', 5000);
            store.Save(settings);
        });

        using var document = JsonDocument.Parse(File.ReadAllBytes(temp.Paths.SettingsFile));
        Assert.True(document.RootElement.TryGetProperty("bulkParallelism", out _));
        Assert.Empty(Directory.GetFiles(temp.Paths.DataDir, "*.tmp"));
        Assert.Null(store.LastError);
    }

    [Fact]
    public void File_saved_with_utf8_bom_by_an_editor_is_read_normally()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        File.WriteAllText(temp.Paths.SettingsFile, "{ \"theme\": \"Light\", \"lastDomain\": \"корп.local\" }", new UTF8Encoding(true));

        var settings = new JsonSettingsStore(temp.Paths).Load();

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("корп.local", settings.LastDomain);
        Assert.False(File.Exists(temp.Paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void Stale_temp_file_does_not_affect_load()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        File.WriteAllText(temp.Paths.SettingsFile, "{ \"theme\": \"Light\" }");
        File.WriteAllText(temp.Paths.SettingsFile + ".abc.tmp", "{ half-written");

        Assert.Equal("Light", new JsonSettingsStore(temp.Paths).Load().Theme);
    }

    [Fact]
    public void Save_failure_does_not_throw_and_keeps_settings_in_memory()
    {
        using var temp = new TempDir();
        // На месте каталога данных лежит файл — создать в нём settings.json нельзя.
        var blocker = Path.Combine(temp.Path, "blocked");
        File.WriteAllText(blocker, "x");
        var store = new JsonSettingsStore(new AppPaths(Path.Combine(blocker, "data")));

        var settings = store.Load();
        settings.Theme = "Dark";
        store.Save(settings);

        Assert.Equal("Dark", store.Load().Theme);
        Assert.NotNull(store.LastError);
    }
}

public class JsonAuditLogTests
{
    private static AuditRecord Record(string target, DateTime timeUtc, ActionStatus status = ActionStatus.Ok) =>
        new()
        {
            TimeUtc = timeUtc,
            Account = @"CORP\adm-ivanov",
            Machine = "ADMIN-PC",
            ActionId = "session.logoff",
            ActionTitle = "Завершить сеанс",
            TargetKind = "session",
            Target = target,
            Status = status,
        };

    private static DateTime Utc(int year, int month, int day, int hour = 12, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    // Пауза после сбоя общей папки в этих тестах отключена: их задача — проверять каждую запись отдельно.
    private static (JsonAuditLog Log, FakeSettingsStore Settings) Create(TempDir temp)
    {
        var settings = new FakeSettingsStore();
        return (new JsonAuditLog(temp.Paths, settings, TimeSpan.FromSeconds(5), TimeSpan.Zero), settings);
    }

    [Fact]
    public async Task Written_record_is_one_json_line_in_monthly_file()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));
        await log.WriteAsync(Record("PC-002", Utc(2026, 1, 16)));
        await log.WriteAsync(Record("PC-003", Utc(2026, 2, 1)));

        var january = await File.ReadAllLinesAsync(Path.Combine(temp.Paths.AuditDir, "2026-01.jsonl"));
        var february = await File.ReadAllLinesAsync(Path.Combine(temp.Paths.AuditDir, "2026-02.jsonl"));
        Assert.Equal(2, january.Length);
        Assert.Single(february);
        Assert.Null(log.LastError);
    }

    [Fact]
    public async Task Enums_are_strings_and_cyrillic_is_readable()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);

        await log.WriteAsync(Record("PC-001 · сессия 1 (Петрова)", Utc(2026, 1, 15), ActionStatus.Failed));

        var text = await File.ReadAllTextAsync(Path.Combine(temp.Paths.AuditDir, "2026-01.jsonl"));
        Assert.Contains("\"status\":\"Failed\"", text);
        Assert.Contains("Завершить сеанс", text);
        Assert.Contains("Петрова", text);
    }

    [Fact]
    public async Task Multiline_text_stays_on_one_line()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)) with { Reason = "первая строка\r\nвторая строка" });

        var lines = await File.ReadAllLinesAsync(Path.Combine(temp.Paths.AuditDir, "2026-01.jsonl"));
        Assert.Single(lines);

        var read = await log.ReadRecentAsync(10);
        Assert.Equal("первая строка\r\nвторая строка", read[0].Reason);
    }

    [Fact]
    public async Task ReadRecent_returns_newest_first_across_months_and_respects_max()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);
        for (var day = 1; day <= 5; day++) await log.WriteAsync(Record($"JAN-{day}", Utc(2026, 1, day)));
        for (var day = 1; day <= 5; day++) await log.WriteAsync(Record($"FEB-{day}", Utc(2026, 2, day)));

        var all = await log.ReadRecentAsync(100);
        var three = await log.ReadRecentAsync(3);

        Assert.Equal(10, all.Count);
        Assert.Equal("FEB-5", all[0].Target);
        Assert.Equal("JAN-1", all[^1].Target);
        Assert.Equal(new[] { "FEB-5", "FEB-4", "FEB-3" }, three.Select(r => r.Target));
    }

    [Fact]
    public async Task ReadRecent_round_trips_all_fields()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);
        var written = Record("PC-001", Utc(2026, 1, 15, 9, 30, 15)) with
        {
            Reason = "обслуживание",
            Message = "готово",
            Details = "shadow без запроса",
            Status = ActionStatus.Cancelled,
        };

        await log.WriteAsync(written);
        var read = (await log.ReadRecentAsync(1)).Single();

        Assert.Equal(written, read);
    }

    [Fact]
    public async Task ReadRecent_filters_by_since()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);
        await log.WriteAsync(Record("old", Utc(2025, 12, 31)));
        await log.WriteAsync(Record("a", Utc(2026, 1, 10)));
        await log.WriteAsync(Record("b", Utc(2026, 1, 20)));
        await log.WriteAsync(Record("c", Utc(2026, 2, 2)));

        var recent = await log.ReadRecentAsync(100, Utc(2026, 1, 15, 0));

        Assert.Equal(new[] { "c", "b" }, recent.Select(r => r.Target));
    }

    [Fact]
    public async Task ReadRecent_skips_broken_lines()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);
        await log.WriteAsync(Record("first", Utc(2026, 1, 1)));
        var file = Path.Combine(temp.Paths.AuditDir, "2026-01.jsonl");
        await File.AppendAllTextAsync(file, "{ это не json\n\n   \n123\n{\"timeUtc\": \"не дата\"}\n", Encoding.UTF8);
        await log.WriteAsync(Record("last", Utc(2026, 1, 2)));

        var read = await log.ReadRecentAsync(100);

        Assert.Equal(new[] { "last", "first" }, read.Select(r => r.Target));
    }

    [Fact]
    public async Task ReadRecent_reads_file_that_starts_with_utf8_bom()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);
        await log.WriteAsync(Record("first", Utc(2026, 1, 1)));
        var file = Path.Combine(temp.Paths.AuditDir, "2026-01.jsonl");
        var bytes = await File.ReadAllBytesAsync(file);
        await File.WriteAllBytesAsync(file, [0xEF, 0xBB, 0xBF, .. bytes]);
        await log.WriteAsync(Record("second", Utc(2026, 1, 2)));

        var read = await log.ReadRecentAsync(10);

        Assert.Equal(new[] { "second", "first" }, read.Select(r => r.Target));
    }

    [Fact]
    public async Task ReadRecent_without_log_directory_returns_empty()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);

        Assert.Empty(await log.ReadRecentAsync(10));
        Assert.Empty(await log.ReadRecentAsync(0));
    }

    [Fact]
    public async Task ReadRecent_ignores_foreign_files_in_audit_dir()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);
        await log.WriteAsync(Record("a", Utc(2026, 1, 1)));
        await File.WriteAllTextAsync(Path.Combine(temp.Paths.AuditDir, "notes.jsonl"), "{}");
        await File.WriteAllTextAsync(Path.Combine(temp.Paths.AuditDir, "2026-13.jsonl"), "{}");

        Assert.Single(await log.ReadRecentAsync(10));
    }

    [Fact]
    public async Task Central_copy_is_appended_to_machine_and_month_file()
    {
        using var temp = new TempDir();
        var central = Path.Combine(temp.Path, "central");
        Directory.CreateDirectory(central);
        var (log, settings) = Create(temp);
        settings.Settings.AuditCentralFolder = central;

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));
        await log.WriteAsync(Record("PC-002", Utc(2026, 1, 16)));

        var file = Path.Combine(central, "ADMIN-PC-2026-01.jsonl");
        var centralLines = await File.ReadAllLinesAsync(file);
        var localLines = await File.ReadAllLinesAsync(Path.Combine(temp.Paths.AuditDir, "2026-01.jsonl"));
        Assert.Equal(localLines, centralLines);
        Assert.Null(log.LastError);
    }

    [Fact]
    public async Task Central_copy_is_not_written_when_folder_is_not_configured()
    {
        using var temp = new TempDir();
        var (log, settings) = Create(temp);
        settings.Settings.AuditCentralFolder = null;

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));

        Assert.Equal(new[] { Path.Combine(temp.Paths.AuditDir, "2026-01.jsonl") }, Directory.GetFiles(temp.Path, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Unavailable_central_folder_does_not_break_local_write()
    {
        using var temp = new TempDir();
        var (log, settings) = Create(temp);
        settings.Settings.AuditCentralFolder = Path.Combine(temp.Path, "no-such-share");

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));

        Assert.Single(await log.ReadRecentAsync(10));
        Assert.NotNull(log.LastError);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "no-such-share")));
    }

    [Fact]
    public async Task Failed_central_folder_is_left_alone_during_cooldown_and_retried_after_it()
    {
        using var temp = new TempDir();
        var central = Path.Combine(temp.Path, "central");
        var settings = new FakeSettingsStore { Settings = { AuditCentralFolder = central } };
        var log = new JsonAuditLog(temp.Paths, settings, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400));

        await log.WriteAsync(Record("first", Utc(2026, 1, 15)));
        Assert.Contains("недоступна", log.LastError);

        Directory.CreateDirectory(central); // общая папка «ожила», но пауза ещё идёт
        await log.WriteAsync(Record("second", Utc(2026, 1, 15)));
        Assert.Empty(Directory.GetFiles(central));
        Assert.NotNull(log.LastError);

        await Task.Delay(500);
        await log.WriteAsync(Record("third", Utc(2026, 1, 15)));

        Assert.Null(log.LastError);
        var centralLines = await File.ReadAllLinesAsync(Path.Combine(central, "ADMIN-PC-2026-01.jsonl"));
        Assert.Single(centralLines);
        Assert.Contains("third", centralLines[0]);
        Assert.Equal(3, (await log.ReadRecentAsync(10)).Count); // локальный журнал полон
    }

    [Fact]
    public async Task Many_writes_to_unavailable_central_folder_are_not_slowed_down()
    {
        using var temp = new TempDir();
        var settings = new FakeSettingsStore { Settings = { AuditCentralFolder = Path.Combine(temp.Path, "no-such-share") } };
        var log = new JsonAuditLog(temp.Paths, settings, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) await log.WriteAsync(Record($"PC-{i}", Utc(2026, 1, 15)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"200 записей заняли {watch.Elapsed.TotalSeconds:0.0} с");
        Assert.Equal(200, (await log.ReadRecentAsync(1000)).Count);
        Assert.NotNull(log.LastError);
    }

    [Fact]
    public async Task Two_log_instances_can_append_to_the_same_central_file()
    {
        using var temp = new TempDir();
        var central = Path.Combine(temp.Path, "central");
        Directory.CreateDirectory(central);
        var first = new JsonAuditLog(new AppPaths(Path.Combine(temp.Path, "a")), new FakeSettingsStore { Settings = { AuditCentralFolder = central } });
        var second = new JsonAuditLog(new AppPaths(Path.Combine(temp.Path, "b")), new FakeSettingsStore { Settings = { AuditCentralFolder = central } });

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            (i % 2 == 0 ? first : second).WriteAsync(Record($"PC-{i:000}", Utc(2026, 1, 15)))));

        var lines = await File.ReadAllLinesAsync(Path.Combine(central, "ADMIN-PC-2026-01.jsonl"));
        Assert.Equal(60, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("{", line));
        Assert.Null(first.LastError);
        Assert.Null(second.LastError);
    }

    [Fact]
    public async Task Central_copy_is_only_ever_appended_to()
    {
        using var temp = new TempDir();
        var central = Path.Combine(temp.Path, "central");
        Directory.CreateDirectory(central);
        var file = Path.Combine(central, "ADMIN-PC-2026-01.jsonl");
        await File.WriteAllTextAsync(file, "строка, записанная раньше\n");
        var (log, settings) = Create(temp);
        settings.Settings.AuditCentralFolder = central;

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));

        var lines = await File.ReadAllLinesAsync(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal("строка, записанная раньше", lines[0]);
    }

    [Fact]
    public async Task Invalid_central_path_does_not_throw()
    {
        using var temp = new TempDir();
        var (log, settings) = Create(temp);
        settings.Settings.AuditCentralFolder = "::\0::";

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));

        Assert.Single(await log.ReadRecentAsync(10));
        Assert.NotNull(log.LastError);
    }

    [Fact]
    public async Task Write_never_throws_and_remembers_last_error_when_local_dir_is_unusable()
    {
        using var temp = new TempDir();
        var blocker = Path.Combine(temp.Path, "blocked");
        await File.WriteAllTextAsync(blocker, "x");
        var log = new JsonAuditLog(new AppPaths(Path.Combine(blocker, "data")), new FakeSettingsStore());

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));

        Assert.NotNull(log.LastError);
    }

    [Fact]
    public async Task Last_error_is_cleared_by_next_successful_write()
    {
        using var temp = new TempDir();
        var (log, settings) = Create(temp);
        settings.Settings.AuditCentralFolder = Path.Combine(temp.Path, "central");

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)));
        Assert.NotNull(log.LastError);

        Directory.CreateDirectory(Path.Combine(temp.Path, "central"));
        await log.WriteAsync(Record("PC-002", Utc(2026, 1, 15)));
        Assert.Null(log.LastError);
    }

    [Fact]
    public async Task Cancelled_token_does_not_throw()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await log.WriteAsync(Record("PC-001", Utc(2026, 1, 15)), cts.Token);
    }

    [Fact]
    public async Task Concurrent_writes_produce_complete_lines()
    {
        using var temp = new TempDir();
        var (log, _) = Create(temp);

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(i => log.WriteAsync(Record($"PC-{i:000}", Utc(2026, 1, 15)))));

        var read = await log.ReadRecentAsync(1000);
        Assert.Equal(100, read.Count);
        Assert.Equal(100, read.Select(r => r.Target).Distinct().Count());
    }

    [Fact]
    public async Task Two_log_instances_can_append_to_the_same_file()
    {
        using var temp = new TempDir();
        var first = new JsonAuditLog(temp.Paths, new FakeSettingsStore());
        var second = new JsonAuditLog(temp.Paths, new FakeSettingsStore());

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            (i % 2 == 0 ? first : second).WriteAsync(Record($"PC-{i:000}", Utc(2026, 1, 15)))));

        Assert.Equal(50, (await first.ReadRecentAsync(1000)).Count);
    }

    [Theory]
    [InlineData("ADMIN-PC", "ADMIN-PC")]
    [InlineData(@"..\evil", "_evil")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("  ", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("...", "unknown")]
    public void Machine_name_is_made_safe_for_file_name(string? machine, string expected) =>
        Assert.Equal(expected, JsonAuditLog.SafeFileName(machine));
}

public class ReverseLineReaderTests
{
    private static async Task<List<string>> ReadAsync(byte[] data, int chunkSize)
    {
        using var stream = new MemoryStream(data);
        var lines = new List<string>();
        await foreach (var line in ReverseLineReader.ReadLinesAsync(stream, CancellationToken.None, chunkSize))
        {
            lines.Add(Encoding.UTF8.GetString(line));
        }

        return lines;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(65536)]
    public async Task Lines_come_in_reverse_order_for_any_chunk_size(int chunkSize)
    {
        var data = Encoding.UTF8.GetBytes("первая\nвторая строка\n\nthird\nпоследняя\n");

        var lines = await ReadAsync(data, chunkSize);

        Assert.Equal(new[] { "последняя", "third", "вторая строка", "первая" }, lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task Missing_trailing_newline_and_crlf_are_handled(int chunkSize)
    {
        var data = Encoding.UTF8.GetBytes("a\r\nb\r\nlast");

        var lines = await ReadAsync(data, chunkSize);

        Assert.Equal(new[] { "last", "b", "a" }, lines);
    }

    [Fact]
    public async Task Empty_file_and_blank_lines_give_nothing()
    {
        Assert.Empty(await ReadAsync(Array.Empty<byte>(), 16));
        Assert.Empty(await ReadAsync(Encoding.UTF8.GetBytes("\n\n\r\n"), 2));
    }

    [Fact]
    public async Task Many_lines_across_many_chunks_are_all_returned()
    {
        var expected = Enumerable.Range(0, 2000).Select(i => $"запись {i} {new string('я', i % 40)}").ToList();
        var data = Encoding.UTF8.GetBytes(string.Join("\n", expected) + "\n");

        var lines = await ReadAsync(data, 97);

        expected.Reverse();
        Assert.Equal(expected, lines);
    }

    [Fact]
    public async Task Cancellation_stops_reading()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("a\nb\n"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ReverseLineReader.ReadLinesAsync(stream, cts.Token))
            {
            }
        });
    }
}

public class SessionCacheTests
{
    private static SessionSnapshot Snapshot(Guid id, DateTime takenAt) =>
        new(id, SessionListResult.Ok(new[] { TestData.Session(1, "ivanov") }), takenAt);

    [Fact]
    public void Set_replaces_snapshot_of_same_computer_and_raises_changed()
    {
        var cache = new SessionCache();
        var id = Guid.NewGuid();
        var changes = 0;
        cache.Changed += (_, _) => changes++;

        cache.Set(Snapshot(id, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var newer = Snapshot(id, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        cache.Set(newer);

        Assert.Equal(2, changes);
        Assert.Same(newer, cache.Get(id));
        Assert.Single(cache.GetAll());
    }

    [Fact]
    public void Get_of_unknown_computer_is_null()
    {
        Assert.Null(new SessionCache().Get(Guid.NewGuid()));
    }

    [Fact]
    public void Clear_empties_cache_and_raises_changed()
    {
        var cache = new SessionCache();
        cache.Set(Snapshot(Guid.NewGuid(), DateTime.UtcNow));
        var changes = 0;
        cache.Changed += (_, _) => changes++;

        cache.Clear();

        Assert.Empty(cache.GetAll());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void GetAll_is_a_snapshot_not_a_live_view()
    {
        var cache = new SessionCache();
        cache.Set(Snapshot(Guid.NewGuid(), DateTime.UtcNow));
        var all = cache.GetAll();

        cache.Set(Snapshot(Guid.NewGuid(), DateTime.UtcNow));

        Assert.Single(all);
        Assert.Equal(2, cache.GetAll().Count);
    }

    [Fact]
    public void Throwing_subscriber_does_not_break_the_cache()
    {
        var cache = new SessionCache();
        var second = 0;
        cache.Changed += (_, _) => throw new InvalidOperationException("подписчик сломался");
        cache.Changed += (_, _) => second++;
        var id = Guid.NewGuid();

        cache.Set(Snapshot(id, DateTime.UtcNow));

        Assert.NotNull(cache.Get(id));
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Parallel_writers_do_not_lose_snapshots()
    {
        var cache = new SessionCache();
        var ids = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(() => cache.Set(Snapshot(id, DateTime.UtcNow)))));

        Assert.Equal(500, cache.GetAll().Count);
    }
}

public class RussianPluralTests
{
    [Theory]
    [InlineData(1, "объекта")]
    [InlineData(2, "объектов")]
    [InlineData(4, "объектов")]
    [InlineData(5, "объектов")]
    [InlineData(11, "объектов")]
    [InlineData(14, "объектов")]
    [InlineData(21, "объекта")]
    [InlineData(22, "объектов")]
    [InlineData(101, "объекта")]
    [InlineData(111, "объектов")]
    public void Chooses_genitive_form(int count, string expected) =>
        Assert.Equal(expected, RussianPlural.Choose(count, "объекта", "объектов", "объектов"));

    [Theory]
    [InlineData(1, "компьютер")]
    [InlineData(3, "компьютера")]
    [InlineData(12, "компьютеров")]
    [InlineData(23, "компьютера")]
    [InlineData(0, "компьютеров")]
    public void Chooses_nominative_form(int count, string expected) =>
        Assert.Equal(expected, RussianPlural.Choose(count, "компьютер", "компьютера", "компьютеров"));
}
