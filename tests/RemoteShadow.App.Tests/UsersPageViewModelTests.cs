using RemoteShadow.App.Pages;
using RemoteShadow.App.Shell;

namespace RemoteShadow.App.Tests;

public sealed class UsersPageViewModelTests
{
    private static UsersPageViewModel CreateViewModel(DemoFixture fixture) =>
        new(fixture.Users, fixture.Runner, fixture.Notifications);

    [Fact]
    public async Task SearchingByLogin_FindsUser_AndSelectingShowsUsersModuleButtons()
    {
        var fixture = new DemoFixture();
        var user = fixture.Data.GetUsers().First(u => !fixture.Data.IsProtected(u.Id));
        var vm = CreateViewModel(fixture);

        vm.SearchText = user.SamAccountName;
        Assert.True(await WaitUntilAsync(() => vm.Results.Count > 0, TimeSpan.FromSeconds(2)));

        vm.SelectedUser = vm.Results.First(u => u.SamAccountName == user.SamAccountName);

        var titles = vm.Buttons.Select(b => b.Title).ToList();
        Assert.Contains("Сбросить пароль", titles);
        Assert.Contains("Добавить в группу…", titles);
        Assert.Contains("Удалить из группы…", titles);

        // Либо «Разблокировать», либо «Включить»/«Отключить» — в зависимости от состояния учётной записи —
        // но хотя бы одно из действий блокировки/включения всегда есть.
        Assert.True(titles.Contains("Разблокировать") || titles.Contains("Включить") || titles.Contains("Отключить"));
    }

    [Fact]
    public async Task SearchText_TooShort_DoesNotQueryDirectory()
    {
        var fixture = new DemoFixture();
        var vm = CreateViewModel(fixture);

        vm.SearchText = "a";
        await Task.Delay(300);

        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task OnNavigatedTo_SelectUserRequest_SelectsUserBySamAccountName()
    {
        var fixture = new DemoFixture();
        var user = fixture.Data.GetUsers().First(u => !fixture.Data.IsProtected(u.Id));
        var vm = CreateViewModel(fixture);

        vm.OnNavigatedTo(new SelectUserRequest(user.SamAccountName));

        Assert.True(await WaitUntilAsync(
            () => string.Equals(vm.SelectedUser?.SamAccountName, user.SamAccountName, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(2)));
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
