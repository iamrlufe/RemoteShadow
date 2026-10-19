using RemoteShadow.App.Pages;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Infrastructure;

namespace RemoteShadow.App.Tests;

/// <summary>Кнопки «Обновить список» и «Обновить доступность», избранное и причина скрытия компьютеров.</summary>
public sealed class ComputersPageRefreshTests
{
    /// <summary>Зонд, у которого «включённость» компьютера меняет тест (имитация включения питания).</summary>
    private sealed class PowerProbe : IHostProbe, IIcmpProbe
    {
        public HashSet<string> PoweredOn { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct) => Task.FromResult(PoweredOn.Contains(host));

        public Task<bool> IsOnlineAsync(string host, TimeSpan timeout, CancellationToken ct) => Task.FromResult(PoweredOn.Contains(host));

        public Task<bool> PingAsync(string hostOrIp, TimeSpan timeout, CancellationToken ct) => Task.FromResult(PoweredOn.Contains(hostOrIp));
    }

    private static (ComputersPageViewModel Vm, FakeComputerIndex Index, PowerProbe Probe, DemoFixture Fixture, List<ComputerInfo> Computers) Create()
    {
        var fixture = new DemoFixture();
        var computers = fixture.Data.GetComputers().Take(6).ToList();
        var index = new FakeComputerIndex(computers);
        var probe = new PowerProbe();
        var availability = new ComputerAvailabilityService(index, probe, probe, fixture.Settings, TimeProvider.System);
        var vm = new ComputersPageViewModel(index, fixture.Sessions, fixture.Runner, fixture.Notifications,
            fixture.Settings, availability, fixture.Scanner, fixture.DeviceMerge, fixture.Dialogs);
        return (vm, index, probe, fixture, computers);
    }

    [Fact]
    public async Task Computer_that_was_off_and_is_now_on_appears_under_the_online_filter_after_refreshing_availability()
    {
        var (vm, index, probe, _, computers) = Create();
        var target = computers[2];

        // Всё выключено: после проверки все «не в сети», но остаются в полном списке AD.
        await vm.RefreshVisibleAvailabilityCommand.ExecuteAsync(null);
        Assert.Equal(computers.Count, vm.Computers.Count);
        Assert.All(index.All, c => Assert.Equal(AvailabilityStatus.Offline, c.AvailabilityStatus));

        vm.AvailabilityFilter = AvailabilityFilter.Online;
        Assert.Empty(vm.Computers);
        Assert.Equal(computers.Count, index.All.Count);
        Assert.Contains("в сети", vm.ActiveFiltersSummary); // причина скрытия названа

        // Компьютер включили. Он скрыт фильтром, но «Обновить доступность» берёт область без фильтра по доступности.
        probe.PoweredOn.Add(target.HostName);
        await vm.RefreshVisibleAvailabilityCommand.ExecuteAsync(null);

        Assert.Contains(vm.Computers, c => c.Id == target.Id);
        Assert.Single(vm.Computers);
        Assert.Equal(AvailabilityStatus.Online, index.Find(target.Id)!.AvailabilityStatus);

        // Выключили снова: обновление возвращает его в «не в сети» и убирает из «Доступных».
        probe.PoweredOn.Clear();
        await vm.RefreshVisibleAvailabilityCommand.ExecuteAsync(null);
        Assert.Empty(vm.Computers);
        Assert.Equal(computers.Count, index.All.Count);
    }

    [Fact]
    public async Task Availability_refresh_ignores_availability_protocol_and_ip_filters_but_keeps_search_and_group_scope()
    {
        var (vm, _, probe, _, computers) = Create();
        foreach (var c in computers) probe.PoweredOn.Add(c.HostName);
        vm.FilterRdp = true;
        vm.IpFilter = "999.";
        vm.SearchText = computers[1].Name;

        await vm.RefreshVisibleAvailabilityCommand.ExecuteAsync(null);

        vm.IpFilter = string.Empty;
        // Проверен только компьютер из области поиска, остальные не трогались.
        Assert.Single(vm.Computers);
        Assert.Equal(computers[1].Id, vm.Computers[0].Id);
        vm.SearchText = string.Empty;
        vm.FilterRdp = false;
        Assert.Equal(1, vm.Computers.Count(c => c.LastCheckedUtc is not null));
    }

    [Fact]
    public async Task Refresh_list_reloads_computers_from_ad_and_shows_new_ones()
    {
        var (vm, index, _, fixture, computers) = Create();
        var added = computers[0] with { Id = Guid.NewGuid(), Name = "NEW-PC-001", DistinguishedName = "CN=NEW-PC-001,OU=Офис,DC=corp,DC=local" };
        index.OnRefresh = list => list.Add(added);
        var before = vm.Computers.Count;

        await vm.RefreshListCommand.ExecuteAsync(null);

        Assert.Equal(1, index.RefreshCount);
        Assert.True(index.LastRefreshWasFull);
        Assert.Equal(before + 1, vm.Computers.Count);
        Assert.Contains(vm.Computers, c => c.Name == "NEW-PC-001");
        Assert.NotEmpty(fixture.Notifications.SuccessMessages);
        Assert.False(vm.IsRefreshingList);
    }

    [Fact]
    public void Only_favorites_filter_shows_favorites_and_names_the_reason()
    {
        var (vm, _, _, fixture, computers) = Create();
        vm.ToggleFavoriteCommand.Execute(computers[3]);

        vm.OnlyFavorites = true;

        Assert.Equal([computers[3].Id], vm.Computers.Select(c => c.Id));
        Assert.Contains("только избранные", vm.ActiveFiltersSummary);
        Assert.Contains(computers[3].Id, fixture.Settings.Load().FavoriteComputers);

        vm.ToggleFavoriteCommand.Execute(computers[3]); // убрали из избранного — список «только избранные» пуст
        Assert.Empty(vm.Computers);

        vm.OnlyFavorites = false;
        Assert.Equal(computers.Count, vm.Computers.Count);
    }

    [Fact]
    public void Active_filters_summary_is_empty_only_when_nothing_narrows_the_list()
    {
        var (vm, _, _, _, _) = Create();
        vm.ShowDisabledAd = true;
        Assert.Null(vm.ActiveFiltersSummary);

        vm.ShowDisabledAd = false;
        Assert.Contains("отключённые в AD", vm.ActiveFiltersSummary);
    }

    [Fact]
    public async Task Statuses_are_checked_automatically_when_the_page_opens_for_a_small_area()
    {
        var (vm, index, probe, _, computers) = Create();
        probe.PoweredOn.Add(computers[0].HostName);
        vm.AutoCheckDelay = TimeSpan.Zero;
        Assert.All(index.All, c => Assert.Equal(AvailabilityStatus.Unknown, c.AvailabilityStatus));

        vm.OnNavigatedTo(null);
        await vm.AutoCheckTask;

        Assert.Equal(AvailabilityStatus.Online, index.Find(computers[0].Id)!.AvailabilityStatus);
        Assert.All(index.All.Skip(1), c => Assert.Equal(AvailabilityStatus.Offline, c.AvailabilityStatus));
        Assert.Null(vm.AutoCheckHint);
    }

    [Fact]
    public async Task Automatic_check_is_skipped_for_a_large_area_and_the_reason_is_shown()
    {
        var (vm, index, _, fixture, computers) = Create();
        var settings = fixture.Settings.Load();
        settings.AutoCheckAvailabilityMax = 2;
        fixture.Settings.Save(settings);
        vm.AutoCheckDelay = TimeSpan.Zero;

        vm.OnNavigatedTo(null);
        await vm.AutoCheckTask;

        Assert.All(index.All, c => Assert.Equal(AvailabilityStatus.Unknown, c.AvailabilityStatus));
        Assert.Contains("Обновить статусы", vm.AutoCheckHint);

        vm.SearchText = computers[0].Name; // область сузилась до лимита — проверка пошла сама
        await vm.AutoCheckTask;
        Assert.Null(vm.AutoCheckHint);
        Assert.NotNull(index.Find(computers[0].Id)!.LastCheckedUtc);
        Assert.Null(index.Find(computers[5].Id)!.LastCheckedUtc);
    }

    [Fact]
    public async Task Automatic_check_can_be_turned_off()
    {
        var (vm, index, _, fixture, _) = Create();
        var settings = fixture.Settings.Load();
        settings.AutoCheckAvailabilityMax = 0;
        fixture.Settings.Save(settings);
        vm.AutoCheckDelay = TimeSpan.Zero;

        vm.OnNavigatedTo(null);
        await vm.AutoCheckTask;

        Assert.All(index.All, c => Assert.Equal(AvailabilityStatus.Unknown, c.AvailabilityStatus));
    }
}
