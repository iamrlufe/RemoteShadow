using System.Diagnostics;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using Xunit.Abstractions;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

/// <summary>Всё, что нужно тесту индекса: каталог AD-заглушка, время под управлением теста, временная папка кэша.</summary>
public sealed class IndexFixture : IDisposable
{
    private readonly List<ComputerIndex> _created = new();

    public IndexFixture()
    {
        Index = Create();
    }

    public TempDir Temp { get; } = new();

    /// <summary>Заглушка каталога AD.</summary>
    public FakeComputerDirectory Ad { get; } = new();

    public FakeSettingsStore Settings { get; } = new();

    public FakeCurrentConnection Connection { get; } = FakeCurrentConnection.LoggedIn();

    public ManualTimeProvider Time { get; } = new();

    public ComputerIndex Index { get; }

    public string CacheFile => Path.Combine(Temp.Paths.CacheDir, "corp.local.json");

    /// <summary>Ещё один индекс на тех же настройках, папке кэша и времени — как после перезапуска программы.</summary>
    public ComputerIndex Create(FakeComputerDirectory? directory = null)
    {
        var index = new ComputerIndex(directory ?? Ad, Settings, Connection, Temp.Paths, Time);
        _created.Add(index);
        return index;
    }

    public IndexFixture Seed(params ComputerInfo[] computers)
    {
        Ad.Computers.AddRange(computers);
        return this;
    }

    public Task FullLoadAsync() => Index.RefreshAsync(full: true, CancellationToken.None);

    public void Dispose()
    {
        foreach (var index in _created) index.Dispose();
        Temp.Dispose();
    }
}

public class ComputerIndexSearchTests
{
    private readonly ITestOutputHelper _output;

    public ComputerIndexSearchTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task<IndexFixture> LoadedAsync(params ComputerInfo[] computers)
    {
        var fx = new IndexFixture().Seed(computers);
        await fx.FullLoadAsync();
        return fx;
    }

    private static string[] Names(IEnumerable<ComputerInfo> computers) => computers.Select(c => c.Name).ToArray();

    [Fact]
    public async Task Ranking_is_exact_then_prefix_then_name_substring_then_description_or_dns()
    {
        using var fx = await LoadedAsync(
            Pc("SRV-PC1", id: 1),
            Pc("NODE7", id: 2, dnsName: "pc1.edge.corp.local"),
            Pc("PC11", id: 3),
            Pc("LAB", id: 4, description: "Стенд рядом с pc1"),
            Pc("PC10", id: 5),
            Pc("PC1", id: 6),
            Pc("OTHER", id: 7));

        var found = fx.Index.Search("pc1");

        Assert.Equal(new[] { "PC1", "PC10", "PC11", "SRV-PC1", "LAB", "NODE7" }, Names(found));
    }

    [Fact]
    public async Task Inside_each_group_results_are_ordered_by_name()
    {
        using var fx = await LoadedAsync(
            Pc("ZED-PC", id: 1),
            Pc("PC-B", id: 2),
            Pc("ALPHA-PC", id: 3),
            Pc("PC-A", id: 4),
            Pc("Z", id: 5, description: "pc"),
            Pc("A", id: 6, description: "pc"));

        var found = fx.Index.Search("pc");

        Assert.Equal(new[] { "PC-A", "PC-B", "ALPHA-PC", "ZED-PC", "A", "Z" }, Names(found));
    }

    [Theory]
    [InlineData("бух")]
    [InlineData("БУХ")]
    [InlineData("Бух")]
    [InlineData("  бух  ")]
    public async Task Cyrillic_and_letter_case_are_ignored(string query)
    {
        using var fx = await LoadedAsync(
            Pc("Бухгалтерия-01", id: 1),
            Pc("СЕРВЕР-БУХ", id: 2),
            Pc("БУХ-ПК2", id: 3),
            Pc("Склад-01", id: 4));

        var found = fx.Index.Search(query);

        Assert.Equal(new[] { "БУХ-ПК2", "Бухгалтерия-01", "СЕРВЕР-БУХ" }, Names(found));
    }

    [Fact]
    public async Task Exact_match_ignores_case_and_beats_longer_names()
    {
        using var fx = await LoadedAsync(Pc("pc-100", id: 1), Pc("PC-10", id: 2), Pc("Pc-1", id: 3));

        Assert.Equal(new[] { "PC-10", "pc-100" }, Names(fx.Index.Search("pc-10")));
        Assert.Equal("Pc-1", fx.Index.Search("PC-1")[0].Name);
    }

    [Fact]
    public async Task Description_and_dns_name_are_searched_case_insensitively()
    {
        using var fx = await LoadedAsync(
            Pc("PC-1", id: 1, description: "Кабинет ДИРЕКТОРА"),
            Pc("PC-2", id: 2, dnsName: "reception.Corp.Local"),
            Pc("PC-3", id: 3));

        Assert.Equal(new[] { "PC-1" }, Names(fx.Index.Search("директора")));
        Assert.Equal(new[] { "PC-2" }, Names(fx.Index.Search("RECEPTION")));
    }

    [Fact]
    public async Task Computer_without_description_or_dns_name_does_not_break_search()
    {
        var bare = Pc("BARE", id: 1) with { Description = null, DnsName = null };
        using var fx = await LoadedAsync(bare, Pc("PC-1", id: 2));

        Assert.Empty(fx.Index.Search("нет такого"));
        Assert.Equal(new[] { "BARE" }, Names(fx.Index.Search("bar")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t \r\n")]
    public async Task Empty_query_gives_empty_list(string query)
    {
        using var fx = await LoadedAsync(Pc("PC-1", id: 1));

        Assert.Empty(fx.Index.Search(query));
    }

    [Fact]
    public async Task Max_limits_result_and_keeps_best_matches()
    {
        using var fx = await LoadedAsync(
            Pc("PC", id: 1),
            Pc("PC-2", id: 2),
            Pc("PC-3", id: 3),
            Pc("X-PC-1", id: 4),
            Pc("X-PC-2", id: 5));

        Assert.Equal(new[] { "PC", "PC-2" }, Names(fx.Index.Search("pc", 2)));
        Assert.Equal(new[] { "PC", "PC-2", "PC-3", "X-PC-1" }, Names(fx.Index.Search("pc", 4)));
        Assert.Empty(fx.Index.Search("pc", 0));
        Assert.Empty(fx.Index.Search("pc", -1));
    }

    [Fact]
    public void Search_on_empty_index_gives_empty_list()
    {
        using var fx = new IndexFixture();

        Assert.Empty(fx.Index.Search("pc"));
        Assert.Empty(fx.Index.All);
    }

    [Fact]
    public async Task Wildcard_characters_are_plain_text()
    {
        using var fx = await LoadedAsync(Pc("PC-1", id: 1), Pc("PC*1", id: 2) with { DnsName = null });

        Assert.Equal(new[] { "PC*1" }, Names(fx.Index.Search("*")));
        Assert.Empty(fx.Index.Search("pc.1"));
    }

    [Fact]
    public async Task Find_returns_record_by_id()
    {
        using var fx = await LoadedAsync(Pc("PC-1", id: 1), Pc("PC-2", id: 2));

        Assert.Equal("PC-2", fx.Index.Find(IdOf(2))?.Name);
        Assert.Null(fx.Index.Find(IdOf(99)));
    }

    [Fact]
    public async Task All_is_sorted_by_name_and_is_not_a_mutable_list()
    {
        using var fx = await LoadedAsync(Pc("b-2", id: 1), Pc("A-1", id: 2), Pc("c-3", id: 3));

        var all = fx.Index.All;

        Assert.Equal(new[] { "A-1", "b-2", "c-3" }, Names(all));
        Assert.False(all is IList<ComputerInfo>);
        Assert.False(all is ComputerInfo[]);
    }

    [Fact]
    public async Task Captured_all_list_is_a_stable_snapshot_of_the_composition()
    {
        using var fx = await LoadedAsync(Pc("PC-1", id: 1));
        var before = fx.Index.All;

        fx.Ad.Computers.Add(Pc("PC-2", id: 2));
        await fx.FullLoadAsync();

        Assert.Single(before);
        Assert.Equal(2, fx.Index.All.Count);
    }

    [Fact]
    public async Task Search_in_10000_computers_is_fast()
    {
        using var fx = new IndexFixture();
        var ous = new[] { AccountingOu, WarehouseOu, OfficeOu, "OU=Серверы," + Root };
        for (var i = 1; i <= 10_000; i++)
        {
            fx.Ad.Computers.Add(Pc(
                $"PC-{i:00000}",
                ous[i % ous.Length],
                id: i,
                description: i % 7 == 0 ? $"Кабинет {i}, бухгалтерия" : null));
        }

        await fx.FullLoadAsync();
        Assert.Equal(10_000, fx.Index.All.Count);

        var queries = new[]
        {
            "pc-04242",         // точное имя
            "pc-0424",          // начало имени
            "4242",             // подстрока имени
            "кабинет 777",      // описание
            "corp.local",       // DNS-имя у всех: самый тяжёлый случай
            "ПК-несуществующий", // ничего не найдено
            "p",                // очень короткий запрос
        };

        foreach (var query in queries) fx.Index.Search(query);

        var timings = new List<double>();
        var worst = 0d;
        for (var round = 0; round < 20; round++)
        {
            foreach (var query in queries)
            {
                var watch = Stopwatch.StartNew();
                var result = fx.Index.Search(query, 50);
                watch.Stop();

                var ms = watch.Elapsed.TotalMilliseconds;
                timings.Add(ms);
                worst = Math.Max(worst, ms);
                Assert.True(result.Count <= 50);
            }
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        _output.WriteLine($"Поиск по 10 000 компьютеров: медиана {median:0.000} мс, худший {worst:0.000} мс, запросов {timings.Count}.");

        Assert.Equal("PC-04242", fx.Index.Search("pc-04242")[0].Name);
        Assert.True(median < 50, $"медиана {median:0.000} мс");
        Assert.True(worst < 150, $"худший запрос {worst:0.000} мс");
    }

    [Fact]
    public async Task Loading_10000_computers_and_ou_lists_are_fast_too()
    {
        using var fx = new IndexFixture();
        for (var i = 1; i <= 10_000; i++)
            fx.Ad.Computers.Add(Pc($"PC-{i:00000}", i % 2 == 0 ? AccountingOu : WarehouseOu, id: i));

        var watch = Stopwatch.StartNew();
        await fx.FullLoadAsync();
        var loadMs = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        var accounting = fx.Index.GetByOu(AccountingOu, includeNested: false);
        var office = fx.Index.GetByOu(OfficeOu, includeNested: true);
        var listMs = watch.Elapsed.TotalMilliseconds;

        _output.WriteLine($"Загрузка 10 000 в индекс: {loadMs:0.0} мс; списки подразделений: {listMs:0.000} мс.");

        Assert.Equal(5_000, accounting.Count);
        Assert.Equal(10_000, office.Count);
        Assert.True(listMs < 150, $"списки подразделений {listMs:0.000} мс");
    }
}
