using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.App.Pages;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;
using RemoteShadow.App.Shell.Converters;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Tests;

/// <summary>
/// Поиск пользователя на компьютерах, главное действие строки, контекстные меню, копирование, сохранение выбора при
/// обновлении списков и положение окна.
/// </summary>
[Collection(ClipboardCollection.Name)]
public sealed class PageInteractionTests
{
    private static async Task<(ComputersPageViewModel Vm, DemoFixture Fixture)> CreateComputersAsync(bool withLocator = true)
    {
        var fixture = new DemoFixture();
        var index = await fixture.GetLoadedIndexAsync();
        var locator = withLocator ? fixture.Provider.GetRequiredService<IUserLocator>() : null;
        var vm = new ComputersPageViewModel(index, fixture.Sessions, fixture.Runner, fixture.Notifications,
            fixture.Settings, fixture.Availability, fixture.Scanner, fixture.DeviceMerge, fixture.Dialogs, locator);
        return (vm, fixture);
    }

    private static (ComputerInfo Computer, SessionInfo Session) ActiveSession(DemoFixture fixture) =>
        fixture.Data.GetComputers()
            .Where(c => fixture.Data.IsOnline(c.Id) && !fixture.Data.IsAccessDenied(c.Id))
            .SelectMany(c => fixture.Data.GetSessions(c.Id).Where(s => s.State == SessionState.Active && s.HasUser).Select(s => (c, s)))
            .First();

    // ----- «Найти пользователя на компьютерах» ---------------------------------------------------------------------

    [Fact]
    public async Task Find_user_request_polls_computers_and_lists_where_the_user_works()
    {
        var (vm, fixture) = await CreateComputersAsync();
        var (computer, session) = ActiveSession(fixture);

        vm.OnNavigatedTo(new FindUserRequest(session.UserName!));
        await vm.FindUserTask;

        Assert.True(vm.HasFindPanel);
        Assert.False(vm.IsFinding);
        Assert.Contains(vm.FoundLocations, l => l.Computer.Id == computer.Id && l.Session.Id == session.Id);
        Assert.Contains("Найдено сессий", vm.FindStatus);
        Assert.DoesNotContain(fixture.Notifications.InfoMessages, m => m.Contains("не реализован"));
    }

    [Fact]
    public async Task Find_user_without_matches_says_so()
    {
        var (vm, _) = await CreateComputersAsync();

        vm.OnNavigatedTo(new FindUserRequest("нет-такого-пользователя-xyz"));
        await vm.FindUserTask;

        Assert.Empty(vm.FoundLocations);
        Assert.Contains("не найдены", vm.FindStatus);
    }

    [Fact]
    public async Task Find_user_without_locator_reports_error_instead_of_pretending()
    {
        var (vm, fixture) = await CreateComputersAsync(withLocator: false);

        vm.OnNavigatedTo(new FindUserRequest("ivanov"));
        await vm.FindUserTask;

        Assert.False(vm.HasFindPanel);
        Assert.Single(fixture.Notifications.ErrorMessages);
    }

    [Fact]
    public async Task Choosing_found_location_selects_computer_and_session_even_if_the_search_hides_it()
    {
        var (vm, fixture) = await CreateComputersAsync();
        var (computer, session) = ActiveSession(fixture);

        vm.SearchText = "такого-имени-нет";
        Assert.Empty(vm.Computers);

        vm.OnNavigatedTo(new FindUserRequest(session.UserName!));
        await vm.FindUserTask;
        vm.SelectedLocation = vm.FoundLocations.First(l => l.Computer.Id == computer.Id && l.Session.Id == session.Id);

        Assert.Equal(computer.Id, vm.SelectedComputer?.Id);
        Assert.Contains(vm.Computers, c => c.Id == computer.Id); // закреплён в списке, хотя поиск его скрывает
        Assert.True(await WaitUntilAsync(() => vm.SelectedSession?.Id == session.Id, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Close_find_hides_the_panel_and_clears_results()
    {
        var (vm, fixture) = await CreateComputersAsync();
        vm.OnNavigatedTo(new FindUserRequest(ActiveSession(fixture).Session.UserName!));
        await vm.FindUserTask;

        vm.CloseFindCommand.Execute(null);

        Assert.False(vm.HasFindPanel);
        Assert.Empty(vm.FoundLocations);
    }

    // ----- главное действие, меню, копирование ---------------------------------------------------------------------

    [Fact]
    public async Task Computer_menu_contains_module_actions_favorites_and_copy_items()
    {
        var (vm, fixture) = await CreateComputersAsync();
        Assert.Null(vm.BuildComputerMenu());

        vm.SelectedComputer = vm.Computers.First(c => c.Id == ActiveSession(fixture).Computer.Id);
        var menu = vm.BuildComputerMenu()!;

        Assert.Contains(menu, i => i.Id == "connection.rdp");
        Assert.Contains(menu, i => i.Id == "local.copy-name" && i.Group == ActionButtons.CopyGroup);
        Assert.Contains(menu, i => i.Id == "local.favorite");
    }

    [Fact]
    public async Task Session_menu_offers_copy_login_and_main_action_starts_shadow()
    {
        var (vm, fixture) = await CreateComputersAsync();
        var (computer, session) = ActiveSession(fixture);
        vm.SelectedComputer = vm.Computers.First(c => c.Id == computer.Id);
        Assert.True(await WaitUntilAsync(() => vm.Sessions.Count > 0, TimeSpan.FromSeconds(2)));
        vm.SelectedSession = vm.Sessions.First(s => s.Id == session.Id);

        var menu = vm.BuildSessionMenu()!;
        Assert.Contains(menu, i => i.Id == "local.copy-login");
        Assert.Contains(menu, i => i.Id == "connection.shadow-view");

        vm.RunSessionMainActionCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => !vm.SessionPresenter.IsBusy && fixture.Notifications.SuccessMessages.Count > 0, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ExecuteFirst_runs_the_first_enabled_button_and_explains_a_disabled_one()
    {
        var notifications = new FakeNotificationService();
        var ran = new List<string>();
        var disabled = new ActionButtonModel("a", "Shadow", "g", null, null, false, "сессия не активна", new RelayCommand(() => ran.Add("a")));
        var enabled = new ActionButtonModel("b", "RDP", "g", null, null, true, null, new RelayCommand(() => ran.Add("b")));

        Assert.False(ActionButtons.ExecuteFirst(new[] { disabled }, notifications, "a"));
        Assert.Contains("сессия не активна", notifications.InfoMessages.Single());

        Assert.True(ActionButtons.ExecuteFirst(new[] { disabled, enabled }, notifications, "a", "b"));
        Assert.Equal(new[] { "b" }, ran);
    }

    [Fact]
    public void Clipboard_copy_reports_success_and_failure()
    {
        var notifications = new FakeNotificationService();
        string? copied = null;
        ClipboardText.Setter = text => { copied = text; return true; };
        try
        {
            Assert.True(ClipboardText.Copy("PC-001", notifications));
            Assert.Equal("PC-001", copied);
            Assert.Contains("PC-001", notifications.SuccessMessages.Single());

            Assert.False(ClipboardText.Copy("  ", notifications)); // пустое не копируется

            ClipboardText.Setter = _ => false;
            Assert.False(ClipboardText.Copy("PC-002", notifications));
            Assert.Single(notifications.ErrorMessages);
        }
        finally
        {
            ClipboardText.Setter = null;
        }
    }

    [Fact]
    public async Task Deleting_a_group_asks_for_confirmation()
    {
        var (vm, fixture) = await CreateComputersAsync();
        vm.NewGroupName = "Тестовая";
        vm.CreateGroupCommand.Execute(null);
        var group = vm.Groups.Single(g => g.Name == "Тестовая");

        fixture.Dialogs.ConfirmResult = false;
        await vm.DeleteGroupCommand.ExecuteAsync(group);
        Assert.Contains(vm.Groups, g => g.Id == group.Id);

        fixture.Dialogs.ConfirmResult = true;
        await vm.DeleteGroupCommand.ExecuteAsync(group);
        Assert.DoesNotContain(vm.Groups, g => g.Id == group.Id);
    }

    [Fact]
    public async Task Subtitle_skips_empty_parts()
    {
        var (vm, _) = await CreateComputersAsync();
        vm.SelectedComputer = vm.Computers.First();

        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedComputerSubtitle));
        Assert.DoesNotContain(" ·  · ", vm.SelectedComputerSubtitle);
        Assert.False(vm.SelectedComputerSubtitle!.EndsWith(" · ", StringComparison.Ordinal));
    }

    // ----- «Диагностика»: отчёт не теряется ------------------------------------------------------------------------

    [Fact]
    public async Task Diagnostics_report_survives_index_refresh_of_the_same_computer()
    {
        var fixture = new DemoFixture();
        var index = new FakeComputerIndex(fixture.Data.GetComputers());
        var vm = new DiagnosticsPageViewModel(index, fixture.Diagnostics, fixture.Runner, fixture.Notifications);
        var computer = fixture.Data.GetComputers().First(c => fixture.Data.IsOnline(c.Id));

        vm.SelectedComputer = vm.Computers.First(c => c.Id == computer.Id);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Report);

        // Как ListBox: при очистке коллекции он записывает в SelectedItem null.
        vm.Computers.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) vm.SelectedComputer = null;
        };

        // Обновился статус «в сети» — запись компьютера новая, но это тот же компьютер; список перестраивается.
        index.SetOnline(computer.Id, false);
        vm.SearchText = computer.Name;

        Assert.Contains(vm.Computers, c => c.Id == computer.Id && c.IsOnline == false);
        Assert.NotNull(vm.Report);
        Assert.Equal(computer.Id, vm.SelectedComputer?.Id);
    }

    [Fact]
    public async Task Diagnostics_selecting_another_computer_cancels_running_check()
    {
        var fixture = new DemoFixture();
        var index = new FakeComputerIndex(fixture.Data.GetComputers());
        var vm = new DiagnosticsPageViewModel(index, fixture.Diagnostics, fixture.Runner, fixture.Notifications);
        var computers = fixture.Data.GetComputers().Where(c => fixture.Data.IsOnline(c.Id)).Take(2).ToList();

        vm.SelectedComputer = vm.Computers.First(c => c.Id == computers[0].Id);
        var run = vm.RunCommand.ExecuteAsync(null);
        vm.SelectedComputer = vm.Computers.First(c => c.Id == computers[1].Id);
        await run;

        // Отчёт первого компьютера не появился под именем второго.
        Assert.Null(vm.Report);
        Assert.False(vm.IsRunning);
    }

    // ----- «Пользователи AD» -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Users_new_search_keeps_selected_user_when_still_found()
    {
        var fixture = new DemoFixture();
        var user = fixture.Data.GetUsers().First(u => !fixture.Data.IsProtected(u.Id));
        var vm = new UsersPageViewModel(fixture.Users, fixture.Runner, fixture.Notifications);

        vm.SearchText = user.SamAccountName;
        Assert.True(await WaitUntilAsync(() => vm.Results.Count > 0, TimeSpan.FromSeconds(2)));
        vm.SelectedUser = vm.Results.First(u => u.SamAccountName == user.SamAccountName);
        Assert.True(await WaitUntilAsync(() => vm.Groups.Count > 0, TimeSpan.FromSeconds(2)));

        // Как ListBox: при очистке коллекции он записывает в SelectedItem null (раньше это сбрасывало карточку,
        // например сразу после перехода из Ctrl+K, когда отложенный поиск перестраивал список).
        vm.Results.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) vm.SelectedUser = null;
        };

        vm.RefreshPage(full: false);
        await Task.Delay(400);

        Assert.Equal(user.SamAccountName, vm.SelectedUser?.SamAccountName);
        Assert.True(vm.HasSelection);
        Assert.NotEmpty(vm.Groups);
        Assert.NotEmpty(vm.Buttons);
        Assert.False(vm.HasNoResults);
        Assert.Contains(user.SamAccountName, vm.SelectedUserSubtitle);
    }

    [Fact]
    public async Task Users_search_without_matches_shows_nothing_found()
    {
        var fixture = new DemoFixture();
        var vm = new UsersPageViewModel(fixture.Users, fixture.Runner, fixture.Notifications);

        vm.SearchText = "нет-такого-пользователя-xyz";

        Assert.True(await WaitUntilAsync(() => vm.HasNoResults, TimeSpan.FromSeconds(2)));
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Users_menu_offers_find_where_logged_on_and_copy()
    {
        var fixture = new DemoFixture();
        var navigation = new FakeNavigationService();
        var user = fixture.Data.GetUsers().First();
        var vm = new UsersPageViewModel(fixture.Users, fixture.Runner, fixture.Notifications, navigation);

        vm.SearchText = user.SamAccountName;
        Assert.True(await WaitUntilAsync(() => vm.Results.Count > 0, TimeSpan.FromSeconds(2)));
        vm.SelectedUser = vm.Results.First(u => u.SamAccountName == user.SamAccountName);

        var menu = vm.BuildUserMenu()!;
        Assert.Contains(menu, i => i.Id == "local.find-user");
        Assert.Contains(menu, i => i.Id == "local.copy-login");

        vm.FindWhereLoggedOnCommand.Execute(null);
        Assert.Equal(PageIds.Computers, navigation.LastPageId);
        Assert.Equal(user.SamAccountName, Assert.IsType<FindUserRequest>(navigation.LastParameter).Text);
    }

    // ----- положение окна и конвертеры ------------------------------------------------------------------------------

    [Fact]
    public void Window_placement_round_trips_and_is_dropped_when_off_screen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rs-window-{Guid.NewGuid():N}.json");
        try
        {
            var store = new WindowStateStore(path);
            Assert.Null(store.Load());

            store.Save(new MainWindowPlacement(100, 50, 1200, 800, true, PageIds.Users));
            var loaded = store.Load();
            Assert.Equal(new MainWindowPlacement(100, 50, 1200, 800, true, PageIds.Users), loaded);

            var screen = new Rect(0, 0, 1920, 1080);
            Assert.Equal(new Rect(100, 50, 1200, 800), WindowStateStore.FitToScreen(loaded!, screen, new Size(960, 600)));
            Assert.Null(WindowStateStore.FitToScreen(loaded! with { Left = 5000 }, screen, new Size(960, 600)));
            Assert.Equal(960, WindowStateStore.FitToScreen(loaded! with { Width = 300 }, screen, new Size(960, 600))!.Value.Width);

            File.WriteAllText(path, "не json");
            Assert.Null(store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Converters_for_loading_and_empty_states()
    {
        var anyTrue = new AnyTrueToVisibilityConverter();
        Assert.Equal(Visibility.Visible, anyTrue.Convert(new object?[] { false, true }, typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Collapsed, anyTrue.Convert(new object?[] { false, false }, typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Visible, anyTrue.Convert(new object?[] { false, false }, typeof(Visibility), "invert", null!));

        var emptyLoaded = new EmptyLoadedToVisibilityConverter();
        Assert.Equal(Visibility.Visible, emptyLoaded.Convert(new object?[] { 0, false, null }, typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Collapsed, emptyLoaded.Convert(new object?[] { 0, true, null }, typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Collapsed, emptyLoaded.Convert(new object?[] { 0, false, "ошибка" }, typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Collapsed, emptyLoaded.Convert(new object?[] { 3, false, null }, typeof(Visibility), null, null!));

        var text = new BoolToTextConverter();
        Assert.Equal("да", text.Convert(true, typeof(string), "да|нет", null!));
        Assert.Equal("нет", text.Convert(false, typeof(string), "да|нет", null!));
        Assert.Equal(string.Empty, text.Convert(false, typeof(string), "да", null!));
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

/// <summary>Тесты, подменяющие статический <see cref="ClipboardText.Setter"/>, не должны идти параллельно.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClipboardCollection
{
    public const string Name = "Clipboard";
}

/// <summary>Запоминает последний переход.</summary>
internal sealed class FakeNavigationService : INavigationService
{
    public string? CurrentPageId => LastPageId;
    public string? LastPageId { get; private set; }
    public object? LastParameter { get; private set; }

    public event EventHandler<NavigationEventArgs>? Navigated;

    public void NavigateTo(string pageId, object? parameter = null)
    {
        LastPageId = pageId;
        LastParameter = parameter;
        Navigated?.Invoke(this, new NavigationEventArgs(pageId, parameter));
    }
}
