using RemoteShadow.App.Pages;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Tests;

public class ComputersPageInventoryTests
{
    private static ComputersPageViewModel NewVm(DemoFixture fixture, FakeInventory inventory, string[] ips, out SubnetDefinition subnet)
    {
        var vm = new ComputersPageViewModel(new FakeComputerIndex(fixture.Data.GetComputers()), fixture.Sessions, fixture.Runner,
            fixture.Notifications, fixture.Settings, fixture.Availability, new ListScanner(ips), fixture.DeviceMerge, fixture.Dialogs,
            inventory: inventory);
        vm.NewSubnetName = "Тест";
        vm.NewSubnetCidr = "10.70.0.0/24";
        vm.AddSubnetCommand.Execute(null);
        subnet = vm.Subnets[0];
        return vm;
    }

    [Fact]
    public async Task Scan_collects_device_info_automatically()
    {
        var fixture = new DemoFixture();
        var inventory = new FakeInventory();
        var vm = NewVm(fixture, inventory, ["10.70.0.1", "10.70.0.2"], out var subnet);

        await vm.ScanSubnetCommand.ExecuteAsync(subnet);
        await inventory.WaitForCallsAsync(2);
        await WaitUntilAsync(() => !vm.IsEnriching);

        Assert.All(vm.DiscoveredDevices, d =>
        {
            Assert.Equal("Microsoft Windows 11 Pro", d.OsName);
            Assert.Equal("WMI", d.NameSource);
            Assert.NotNull(d.EnrichedUtc);
        });
    }

    [Fact]
    public async Task Auto_collection_can_be_switched_off()
    {
        var fixture = new DemoFixture();
        var settings = fixture.Settings.Load();
        settings.AutoCollectDeviceInfo = false;
        fixture.Settings.Save(settings);
        var inventory = new FakeInventory();
        var vm = NewVm(fixture, inventory, ["10.70.0.1"], out var subnet);

        await vm.ScanSubnetCommand.ExecuteAsync(subnet);

        Assert.Equal(0, inventory.Calls);
        Assert.True(vm.CanResumeEnrichment);
    }

    [Fact]
    public async Task Pause_stops_collection_and_resume_finishes_the_rest()
    {
        var fixture = new DemoFixture();
        var inventory = new FakeInventory { Gate = new SemaphoreSlim(0) };
        var ips = Enumerable.Range(1, 6).Select(i => $"10.70.0.{i}").ToArray();
        var vm = NewVm(fixture, inventory, ips, out var subnet);

        await vm.ScanSubnetCommand.ExecuteAsync(subnet);
        await inventory.WaitForCallsAsync(ComputersPageViewModel.InventoryParallelism);
        Assert.True(vm.IsEnriching);

        vm.PauseEnrichmentCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsEnriching);

        Assert.True(vm.IsEnrichPaused);
        Assert.True(vm.CanResumeEnrichment);
        Assert.Contains("приостановлен", vm.EnrichStatus);

        inventory.Gate = null;
        await vm.ResumeEnrichmentCommand.ExecuteAsync(null);

        Assert.False(vm.CanResumeEnrichment);
        Assert.All(vm.DiscoveredDevices, d => Assert.NotNull(d.EnrichedUtc));
    }

    [Fact]
    public async Task Failed_inventory_keeps_the_reason_and_is_not_retried_automatically()
    {
        var fixture = new DemoFixture();
        var inventory = new FakeInventory { Error = "WinRM закрыт (порт 5985)" };
        var vm = NewVm(fixture, inventory, ["10.70.0.1"], out var subnet);

        await vm.ScanSubnetCommand.ExecuteAsync(subnet);
        await WaitUntilAsync(() => !vm.IsEnriching && inventory.Calls == 1);

        var device = Assert.Single(vm.DiscoveredDevices);
        Assert.Equal("WinRM закрыт (порт 5985)", device.EnrichError);
        Assert.False(vm.CanResumeEnrichment);
        vm.SelectedDiscovered = device;
        Assert.Contains("не получены", vm.SelectedDiscoveredText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "условие не выполнилось за 5 секунд");
    }

    private sealed class ListScanner(string[] ips) : ISubnetScanner
    {
        public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
            SubnetDefinition subnet, IProgress<ScanProgress>? progress,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var ip in ips)
            {
                await Task.Yield();
                yield return new DiscoveredDevice { Key = ip, SubnetId = subnet.Id, IpAddress = ip, FoundInLastScan = true, IsOnline = true };
            }
        }
    }

    private sealed class FakeInventory : IDeviceInventoryService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public SemaphoreSlim? Gate { get; set; }
        public string? Error { get; set; }

        public async Task WaitForCallsAsync(int count)
        {
            for (var i = 0; i < 200 && Calls < count; i++) await Task.Delay(25);
        }

        public async Task<DeviceInventory> CollectAsync(string host, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Gate is { } gate) await gate.WaitAsync(ct);
            if (Error is not null) return new DeviceInventory { Error = Error };
            return new DeviceInventory { Hostname = "WS-" + host.Split('.')[^1], OsName = "Microsoft Windows 11 Pro", OsVersion = "10.0.26100" };
        }
    }
}
