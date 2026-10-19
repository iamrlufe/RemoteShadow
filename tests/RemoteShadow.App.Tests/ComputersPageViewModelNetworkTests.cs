using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.App.Pages;
using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Tests;

/// <summary>Фильтры/сортировка, избранное, группы, подсети/сканирование и ручное обновление доступности на странице «Компьютеры».</summary>
public sealed class ComputersPageViewModelNetworkTests
{
    private static ComputersPageViewModel CreateViewModel(DemoFixture fixture, IEnumerable<ComputerInfo> computers) =>
        new(new FakeComputerIndex(computers), fixture.Sessions, fixture.Runner, fixture.Notifications,
            fixture.Settings, fixture.Availability, fixture.Scanner, fixture.DeviceMerge, fixture.Dialogs);

    [Fact]
    public void Name_filter_hides_non_matching_computers()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        var target = fixture.Data.GetComputers().First();

        vm.NameFilter = target.Name;

        Assert.All(vm.Computers, c => Assert.Contains(target.Name, c.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.Computers, c => c.Id == target.Id);
    }

    [Fact]
    public void Filter_changes_are_persisted_and_reloaded_by_a_new_view_model()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.NameFilter = "pc-0";
        vm.AvailabilityFilter = AvailabilityFilter.Online;
        vm.SortBy = ComputersSortBy.Name;
        vm.SortDescending = true;

        var reloaded = CreateViewModel(fixture, fixture.Data.GetComputers());

        Assert.Equal("pc-0", reloaded.NameFilter);
        Assert.Equal(AvailabilityFilter.Online, reloaded.AvailabilityFilter);
        Assert.Equal(ComputersSortBy.Name, reloaded.SortBy);
        Assert.True(reloaded.SortDescending);
    }

    [Fact]
    public void Toggle_favorite_updates_settings()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        var computer = fixture.Data.GetComputers().First();

        Assert.False(vm.IsFavorite(computer));
        vm.ToggleFavoriteCommand.Execute(computer);
        Assert.True(vm.IsFavorite(computer));
        Assert.Contains(computer.Id, fixture.Settings.Load().FavoriteComputers);

        vm.ToggleFavoriteCommand.Execute(computer);
        Assert.False(vm.IsFavorite(computer));
    }

    [Fact]
    public void Create_rename_and_delete_group_round_trip_through_settings()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.NewGroupName = "Бухгалтерия";
        vm.CreateGroupCommand.Execute(null);

        var group = Assert.Single(vm.Groups);
        Assert.Equal("Бухгалтерия", group.Name);
        Assert.Single(fixture.Settings.Load().ComputerGroups);

        vm.DeleteGroupCommand.Execute(group);

        Assert.Empty(vm.Groups);
        Assert.Empty(fixture.Settings.Load().ComputerGroups);
    }

    [Fact]
    public void Deleting_a_group_does_not_remove_the_computer_from_ad()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        var computer = fixture.Data.GetComputers().First();
        vm.SelectedComputer = vm.Computers.First(c => c.Id == computer.Id);

        vm.NewGroupName = "Temp";
        vm.CreateGroupCommand.Execute(null);
        var group = vm.Groups[0];
        vm.AddSelectedComputerToGroupCommand.Execute(group);
        Assert.Contains(computer.Id, group.ComputerIds);

        vm.DeleteGroupCommand.Execute(group);

        Assert.Contains(fixture.Data.GetComputers(), c => c.Id == computer.Id); // компьютер остаётся в AD
    }

    [Fact]
    public void Computer_can_belong_to_several_groups_at_once()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        var computer = fixture.Data.GetComputers().First();
        vm.SelectedComputer = vm.Computers.First(c => c.Id == computer.Id);

        vm.NewGroupName = "Group A";
        vm.CreateGroupCommand.Execute(null);
        vm.NewGroupName = "Group B";
        vm.CreateGroupCommand.Execute(null);

        foreach (var group in vm.Groups) vm.AddSelectedComputerToGroupCommand.Execute(group);

        Assert.All(vm.Groups, g => Assert.Contains(computer.Id, g.ComputerIds));
    }

    [Fact]
    public void Selecting_a_group_filters_the_computer_list()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        var computer = fixture.Data.GetComputers().First();
        vm.SelectedComputer = vm.Computers.First(c => c.Id == computer.Id);
        vm.NewGroupName = "Only-this-one";
        vm.CreateGroupCommand.Execute(null);
        vm.AddSelectedComputerToGroupCommand.Execute(vm.Groups[0]);

        vm.SelectedGroup = vm.Groups[0];

        Assert.Single(vm.Computers);
        Assert.Equal(computer.Id, vm.Computers[0].Id);
    }

    [Fact]
    public void Clearing_group_filter_restores_the_full_computer_list()
    {
        var fixture = new DemoFixture();
        var all = fixture.Data.GetComputers().ToList();
        var vm = CreateViewModel(fixture, all);
        vm.SelectedComputer = vm.Computers.First();
        vm.NewGroupName = "Only-this-one";
        vm.CreateGroupCommand.Execute(null);
        vm.AddSelectedComputerToGroupCommand.Execute(vm.Groups[0]);
        vm.SelectedGroup = vm.Groups[0];
        Assert.True(vm.HasSelectedGroup);
        Assert.Single(vm.Computers);
        Assert.Equal($"Показано 1 из {all.Count}", vm.ListSummary);

        vm.ClearGroupFilterCommand.Execute(null);

        Assert.Null(vm.SelectedGroup);
        Assert.False(vm.HasSelectedGroup);
        Assert.True(vm.Computers.Count > 1);
        Assert.Equal($"Показано {vm.Computers.Count} из {all.Count}", vm.ListSummary);
        Assert.Contains(vm.Groups, g => g.Name == "Only-this-one"); // группа сама не удаляется
    }

    [Fact]
    public void Creating_a_group_with_an_existing_name_is_rejected()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        vm.NewGroupName = "Бухгалтерия";
        vm.CreateGroupCommand.Execute(null);

        vm.NewGroupName = "бухгалтерия ";
        vm.CreateGroupCommand.Execute(null);

        Assert.Single(vm.Groups);
        Assert.Single(fixture.Settings.Load().ComputerGroups);
        Assert.Single(fixture.Notifications.ErrorMessages);
    }

    [Fact]
    public async Task Rescan_marks_devices_missing_from_the_latest_scan_as_not_found()
    {
        var fixture = new DemoFixture();
        var scanner = new SequenceScanner();
        var vm = new ComputersPageViewModel(new FakeComputerIndex(fixture.Data.GetComputers()), fixture.Sessions, fixture.Runner,
            fixture.Notifications, fixture.Settings, fixture.Availability, scanner, fixture.DeviceMerge, fixture.Dialogs);
        vm.NewSubnetName = "Тест";
        vm.NewSubnetCidr = "10.60.0.0/24";
        vm.AddSubnetCommand.Execute(null);
        var subnet = vm.Subnets[0];
        scanner.Next = ["10.60.0.1", "10.60.0.2"];
        await vm.ScanSubnetCommand.ExecuteAsync(subnet);
        Assert.All(vm.DiscoveredDevices, d => Assert.True(d.FoundInLastScan));

        scanner.Next = ["10.60.0.1"];
        await vm.ScanSubnetCommand.ExecuteAsync(subnet);

        Assert.Equal(2, vm.DiscoveredDevices.Count);
        Assert.True(vm.DiscoveredDevices.Single(d => d.IpAddress == "10.60.0.1").FoundInLastScan);
        Assert.False(vm.DiscoveredDevices.Single(d => d.IpAddress == "10.60.0.2").FoundInLastScan);
    }

    private sealed class SequenceScanner : RemoteShadow.Core.Services.ISubnetScanner
    {
        public string[] Next { get; set; } = [];

        public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
            SubnetDefinition subnet, IProgress<RemoteShadow.Core.Services.ScanProgress>? progress,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var ip in Next)
            {
                await Task.Yield();
                yield return new DiscoveredDevice { Key = ip, SubnetId = subnet.Id, IpAddress = ip, FoundInLastScan = true, IsOnline = true };
            }
        }
    }

    [Fact]
    public void Computer_can_be_added_to_an_empty_group_without_using_it_as_the_list_filter()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        vm.NewGroupName = "Пустая";
        vm.CreateGroupCommand.Execute(null);
        var computer = vm.Computers.First();
        vm.SelectedComputer = computer;
        vm.MembershipGroup = vm.Groups[0];

        vm.AddSelectedComputerToGroupCommand.Execute(vm.MembershipGroup);

        Assert.Null(vm.SelectedGroup);
        Assert.Same(computer, vm.SelectedComputer); // список не сузился, выбор не потерян
        Assert.Contains(computer.Id, fixture.Settings.Load().ComputerGroups[0].ComputerIds);

        vm.RemoveComputerFromGroupCommand.Execute(vm.MembershipGroup);

        Assert.Empty(fixture.Settings.Load().ComputerGroups[0].ComputerIds);
    }

    [Fact]
    public void Adding_the_same_network_twice_is_rejected_even_if_written_differently()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        vm.NewSubnetName = "Офис";
        vm.NewSubnetCidr = "10.80.0.0/24";
        vm.AddSubnetCommand.Execute(null);

        vm.NewSubnetName = "Офис копия";
        vm.NewSubnetCidr = "10.80.0.77/24"; // та же сеть 10.80.0.0/24
        vm.AddSubnetCommand.Execute(null);
        vm.NewSubnetName = "Другой префикс";
        vm.NewSubnetCidr = "10.80.0.0/25";
        vm.AddSubnetCommand.Execute(null);

        Assert.Equal(["10.80.0.0/24", "10.80.0.0/25"], fixture.Settings.Load().Subnets.Select(s => s.Cidr));
        Assert.Single(fixture.Notifications.ErrorMessages);
    }

    [Fact]
    public void Protocol_filter_on_unchecked_computers_explains_the_empty_list()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        Assert.Null(vm.EmptyListHint);

        vm.FilterRdp = true; // доступность ещё ни разу не проверялась

        Assert.Empty(vm.Computers);
        Assert.NotNull(vm.EmptyListHint);

        vm.FilterRdp = false;
        Assert.Null(vm.EmptyListHint);
    }

    [Fact]
    public void Adding_a_subnet_validates_cidr()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.NewSubnetName = "Офис";
        vm.NewSubnetCidr = "not-a-cidr";
        vm.AddSubnetCommand.Execute(null);

        Assert.Empty(vm.Subnets);
        Assert.Single(fixture.Notifications.ErrorMessages);

        vm.NewSubnetCidr = "10.50.0.0/24";
        vm.AddSubnetCommand.Execute(null);

        Assert.Single(vm.Subnets);
        Assert.Single(fixture.Settings.Load().Subnets);
    }

    [Fact]
    public void Deleting_a_subnet_removes_it_from_settings()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());
        vm.NewSubnetName = "Офис";
        vm.NewSubnetCidr = "10.50.0.0/24";
        vm.AddSubnetCommand.Execute(null);
        var subnet = vm.Subnets[0];

        vm.DeleteSubnetCommand.Execute(subnet);

        Assert.Empty(vm.Subnets);
        Assert.Empty(fixture.Settings.Load().Subnets);
    }

    [Fact]
    public async Task Scanning_a_subnet_finds_devices_and_merges_matches_with_ad()
    {
        var fixture = new DemoFixture();
        var index = await fixture.GetLoadedIndexAsync();
        var vm = new ComputersPageViewModel(index, fixture.Sessions, fixture.Runner, fixture.Notifications,
            fixture.Settings, fixture.Availability, fixture.Scanner, fixture.DeviceMerge, fixture.Dialogs);
        var online = fixture.Data.GetComputers().First(c => fixture.Data.IsOnline(c.Id));
        await fixture.Provider.GetRequiredService<RemoteShadow.Core.Services.IDnsResolverService>().ResolveAsync(online.HostName, CancellationToken.None);

        vm.NewSubnetName = "Офис";
        vm.NewSubnetCidr = Demo.DemoNetworkMap.OfficeCidr;
        vm.AddSubnetCommand.Execute(null);

        await vm.ScanSubnetCommand.ExecuteAsync(vm.Subnets[0]);

        Assert.NotEmpty(vm.DiscoveredDevices);
        Assert.Contains(vm.DiscoveredDevices, d => d.MatchedComputerId == online.Id);
    }

    [Fact]
    public async Task Rescanning_updates_existing_record_instead_of_duplicating()
    {
        var fixture = new DemoFixture();
        var index = await fixture.GetLoadedIndexAsync();
        var vm = new ComputersPageViewModel(index, fixture.Sessions, fixture.Runner, fixture.Notifications,
            fixture.Settings, fixture.Availability, fixture.Scanner, fixture.DeviceMerge, fixture.Dialogs);
        vm.NewSubnetName = "Офис";
        vm.NewSubnetCidr = "10.50.0.0/26";
        vm.AddSubnetCommand.Execute(null);

        await vm.ScanSubnetCommand.ExecuteAsync(vm.Subnets[0]);
        var firstCount = vm.DiscoveredDevices.Count;
        await vm.ScanSubnetCommand.ExecuteAsync(vm.Subnets[0]);

        Assert.Equal(firstCount, vm.DiscoveredDevices.Count);
    }

    [Fact]
    public async Task Refresh_visible_availability_updates_computers_without_scanning_a_subnet()
    {
        var fixture = new DemoFixture();
        var index = await fixture.GetLoadedIndexAsync();
        var vm = new ComputersPageViewModel(index, fixture.Sessions, fixture.Runner, fixture.Notifications,
            fixture.Settings, fixture.Availability, fixture.Scanner, fixture.DeviceMerge, fixture.Dialogs);

        await vm.RefreshVisibleAvailabilityCommand.ExecuteAsync(null);

        Assert.All(vm.Computers.Take(5), c => Assert.NotNull(c.LastCheckedUtc));
    }
}
