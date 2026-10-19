using RemoteShadow.App.Pages;
using RemoteShadow.App.Shell;
using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Tests;

public sealed class ComputersPageViewModelTests
{
    private static ComputerInfo OnlineWorkstationWithActiveSession(DemoFixture fixture) =>
        fixture.Data.GetComputers().First(c =>
            !IsServer(c) && fixture.Data.IsOnline(c.Id) && !fixture.Data.IsAccessDenied(c.Id)
            && fixture.Data.GetSessions(c.Id).Any(s => s.State == SessionState.Active && s.HasUser));

    private static bool IsServer(ComputerInfo c) => c.OperatingSystem?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true;

    private static ComputersPageViewModel CreateViewModel(DemoFixture fixture, IEnumerable<ComputerInfo> computers) =>
        new(new FakeComputerIndex(computers), fixture.Sessions, fixture.Runner, fixture.Notifications,
            fixture.Settings, fixture.Availability, fixture.Scanner, fixture.DeviceMerge, fixture.Dialogs);

    [Fact]
    public void SelectingComputer_ShowsConnectionAndPowerButtons()
    {
        var fixture = new DemoFixture();
        var computer = fixture.Data.GetComputers().First();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.SelectedComputer = vm.Computers.First(c => c.Id == computer.Id);

        var titles = vm.ComputerButtons.Select(b => b.Title).ToList();
        Assert.Contains("RDP", titles);
        Assert.Contains("SSH", titles);
        Assert.Contains("Перезагрузить", titles);
        Assert.Contains("Выключить", titles);
    }

    [Fact]
    public async Task SelectingComputer_LoadsSessions_AndSelectingActiveSession_ShowsShadowButtons()
    {
        var fixture = new DemoFixture();
        var computer = OnlineWorkstationWithActiveSession(fixture);
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.SelectedComputer = vm.Computers.First(c => c.Id == computer.Id);

        Assert.True(await WaitUntilAsync(() => vm.Sessions.Count > 0, TimeSpan.FromSeconds(2)));

        var active = vm.Sessions.First(s => s.State == SessionState.Active && s.HasUser);
        vm.SelectedSession = active;

        var titles = vm.SessionButtons.Select(b => b.Title).ToList();
        Assert.Contains("Shadow: просмотр", titles);
        Assert.Contains("Shadow: управление", titles);
        Assert.Contains("Сообщение", titles);
        Assert.Contains("Отключить", titles);
        Assert.Contains("Выйти из системы", titles);
    }

    [Fact]
    public void OnNavigatedTo_FindUserRequest_ReportsFeatureNotImplementedInsteadOfPretending()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.OnNavigatedTo(new FindUserRequest("петрова"));

        Assert.Single(fixture.Notifications.InfoMessages);
        Assert.Contains("не реализован", fixture.Notifications.InfoMessages[0]);
    }

    [Fact]
    public void OnNavigatedTo_SelectComputerRequest_UnknownId_ReportsError()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.OnNavigatedTo(new SelectComputerRequest(Guid.NewGuid()));

        Assert.Single(fixture.Notifications.ErrorMessages);
        Assert.Null(vm.SelectedComputer);
    }

    [Fact]
    public async Task OnNavigatedTo_SelectComputerRequest_SelectsComputerAndRequestedSession()
    {
        var fixture = new DemoFixture();
        var computer = OnlineWorkstationWithActiveSession(fixture);
        var sessionId = fixture.Data.GetSessions(computer.Id).First(s => s.State == SessionState.Active && s.HasUser).Id;
        var vm = CreateViewModel(fixture, fixture.Data.GetComputers());

        vm.OnNavigatedTo(new SelectComputerRequest(computer.Id, sessionId));

        Assert.Equal(computer.Id, vm.SelectedComputer?.Id);
        Assert.True(await WaitUntilAsync(() => vm.SelectedSession?.Id == sessionId, TimeSpan.FromSeconds(2)));
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
