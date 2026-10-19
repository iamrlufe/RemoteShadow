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

    [Fact]
    public async Task Report_text_lists_every_check_and_hints_for_problems()
    {
        var fixture = new DemoFixture();
        var computer = FindOnlineWithScenario(fixture, DemoDiagnosticScenario.TermServiceStopped);
        var vm = CreateViewModel(fixture, out var index);
        Assert.Equal(string.Empty, vm.BuildReportText());

        vm.SelectedComputer = index.Find(computer.Id);
        await vm.RunCommand.ExecuteAsync(null);

        var text = vm.BuildReportText();
        Assert.Contains(computer.Name, text);
        Assert.Contains("Вывод:", text);
        Assert.Contains("Что исправить:", text);
        Assert.All(vm.Report!.Items, item => Assert.Contains(item.Title, text));
        Assert.True(vm.CopyReportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Verdict_reflects_the_worst_check_status()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, out var index);
        Assert.Null(vm.VerdictStatus);
        Assert.False(vm.CopyReportCommand.CanExecute(null));

        vm.SelectedComputer = index.Find(FindOnlineWithScenario(fixture, DemoDiagnosticScenario.Healthy).Id);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(DiagnosticStatus.Ok, vm.VerdictStatus);
        Assert.False(string.IsNullOrWhiteSpace(vm.VerdictText));

        vm.SelectedComputer = index.Find(FindOnlineWithScenario(fixture, DemoDiagnosticScenario.TermServiceStopped).Id);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.VerdictStatus is DiagnosticStatus.Fail or DiagnosticStatus.Warning);
    }
}
