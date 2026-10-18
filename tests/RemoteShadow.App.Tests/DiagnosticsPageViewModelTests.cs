using RemoteShadow.App.Pages;
using RemoteShadow.App.Shell;
using RemoteShadow.Core.Models;
using RemoteShadow.Demo;

namespace RemoteShadow.App.Tests;

public sealed class DiagnosticsPageViewModelTests
{
    private static DiagnosticsPageViewModel CreateViewModel(DemoFixture fixture, out FakeComputerIndex index)
    {
        index = new FakeComputerIndex(fixture.Data.GetComputers());
        return new DiagnosticsPageViewModel(index, fixture.Diagnostics, fixture.Runner, fixture.Notifications);
    }

    private static ComputerInfo FindOnlineWithScenario(DemoFixture fixture, DemoDiagnosticScenario scenario) =>
        fixture.Data.GetComputers().First(c =>
            fixture.Data.IsOnline(c.Id) && DemoDiagnosticsService.GetScenario(c.Name) == scenario);

    [Fact]
    public async Task Run_TermServiceStopped_ProducesFixButton()
    {
        var fixture = new DemoFixture();
        var computer = FindOnlineWithScenario(fixture, DemoDiagnosticScenario.TermServiceStopped);
        var vm = CreateViewModel(fixture, out var index);

        vm.SelectedComputer = index.Find(computer.Id);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Report);
        var fixable = vm.Items.Where(i => i.FixButton is not null).ToList();
        Assert.Single(fixable);
        Assert.Equal("Исправить", fixable[0].FixButton!.Title);
    }

    [Fact]
    public async Task Run_Healthy_ProducesNoFixButtons()
    {
        var fixture = new DemoFixture();
        var computer = FindOnlineWithScenario(fixture, DemoDiagnosticScenario.Healthy);
        var vm = CreateViewModel(fixture, out var index);

        vm.SelectedComputer = index.Find(computer.Id);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Report);
        Assert.NotEmpty(vm.Items);
        Assert.All(vm.Items, i => Assert.Null(i.FixButton));
    }

    [Fact]
    public void RunCommand_WithoutSelectedComputer_IsDisabled()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, out _);

        Assert.False(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task OnNavigatedTo_DiagnoseComputerRequest_SelectsAndRunsAutomatically()
    {
        var fixture = new DemoFixture();
        var computer = FindOnlineWithScenario(fixture, DemoDiagnosticScenario.Healthy);
        var vm = CreateViewModel(fixture, out _);

        vm.OnNavigatedTo(new DiagnoseComputerRequest(computer.Id));

        Assert.True(await WaitUntilAsync(() => vm.Report is not null, TimeSpan.FromSeconds(2)));
        Assert.Equal(computer.HostName, vm.Report!.Host);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(20);
        }

        return true;
    }
}
