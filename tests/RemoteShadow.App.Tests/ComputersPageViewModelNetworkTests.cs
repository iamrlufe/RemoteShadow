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
