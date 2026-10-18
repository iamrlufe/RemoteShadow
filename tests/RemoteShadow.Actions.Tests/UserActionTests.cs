using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class UserStateActionTests
{
    [Fact]
    public void Metadata()
    {
        var unlock = new UnlockAction();
        Assert.Equal(("Разблокировать", "lock-open", ActionDanger.Confirm, true), (unlock.Title, unlock.Icon, unlock.Danger, unlock.SupportsBulk));

        var enable = new EnableAction();
        Assert.Equal(("Включить", "user-check", ActionDanger.Confirm, true), (enable.Title, enable.Icon, enable.Danger, enable.SupportsBulk));

        var disable = new DisableAction();
        Assert.Equal(("Отключить", "user-off", ActionDanger.ConfirmWithReason, true), (disable.Title, disable.Icon, disable.Danger, disable.SupportsBulk));

        Assert.All(new IAction<UserTarget>[] { unlock, enable, disable }, a => Assert.Equal("Пользователь AD", a.Group));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void CanExecute_depends_on_account_state(bool enabled, bool lockedOut)
    {
        var env = new TestEnv();
        var target = Samples.UserTarget(Samples.User(enabled: enabled, lockedOut: lockedOut));

        var unlock = new UnlockAction();
        Assert.Equal(lockedOut, unlock.CanExecute(target, env.Context()));
        Assert.Equal(lockedOut ? null : "Учётная запись не заблокирована", unlock.GetUnavailableReason(target, env.Context()));

        var enable = new EnableAction();
        Assert.Equal(!enabled, enable.CanExecute(target, env.Context()));
        Assert.Equal(enabled ? "Учётная запись уже включена" : null, enable.GetUnavailableReason(target, env.Context()));

        var disable = new DisableAction();
        Assert.Equal(enabled, disable.CanExecute(target, env.Context()));
        Assert.Equal(enabled ? null : "Учётная запись уже отключена", disable.GetUnavailableReason(target, env.Context()));
    }

    [Fact]
    public async Task Unlock_calls_directory()
    {
        var env = new TestEnv();
        var user = Samples.User(lockedOut: true);

        var result = await new UnlockAction().ExecuteAsync(Samples.UserTarget(user), env.Context(), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(user.DistinguishedName, Assert.Single(env.Users.Unlocks));
    }

    [Fact]
    public async Task Enable_and_disable_set_flag()
    {
        var env = new TestEnv();
        var user = Samples.User(enabled: false);

        await new EnableAction().ExecuteAsync(Samples.UserTarget(user), env.Context(), CancellationToken.None);
        await new DisableAction().ExecuteAsync(Samples.UserTarget(user), env.Context(), CancellationToken.None);

        Assert.Equal([(user.DistinguishedName, true), (user.DistinguishedName, false)], env.Users.EnabledChanges);
    }
}

public class ResetPasswordActionTests
{
    private const string Secret = "S3cret!Pass-xyz";

    [Fact]
    public void Metadata()
    {
        var a = new ResetPasswordAction();
        Assert.Equal("Сбросить пароль", a.Title);
        Assert.Equal("key", a.Icon);
        Assert.Equal("Пользователь AD", a.Group);
        Assert.Equal(ActionDanger.ConfirmWithReason, a.Danger);
        Assert.False(a.SupportsBulk);
        Assert.True(a.CanExecute(Samples.UserTarget(), new TestEnv().Context()));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task Prepare_stores_password_and_flag(bool mustChange, string expectedFlag)
    {
        var env = new TestEnv();
        env.Dialogs.PasswordAnswer = new PasswordChoice(Secret, mustChange);

        var parameters = await new ResetPasswordAction().PrepareAsync(
            [Samples.UserTarget(Samples.User(displayName: "Петрова А."))], env.Context(), CancellationToken.None);

        Assert.Equal(Secret, parameters!["password"]);
        Assert.Equal(expectedFlag, parameters["mustChange"]);
        Assert.Equal("Петрова А.", Assert.Single(env.Dialogs.PasswordPrompts));
    }

    [Fact]
    public async Task Prepare_uses_login_when_no_display_name()
    {
        var env = new TestEnv();

        await new ResetPasswordAction().PrepareAsync(
            [Samples.UserTarget(Samples.User(sam: "svc-backup", displayName: null))], env.Context(), CancellationToken.None);

        Assert.Equal("svc-backup", Assert.Single(env.Dialogs.PasswordPrompts));
    }

    [Fact]
    public async Task Prepare_cancel_returns_null()
    {
        var env = new TestEnv();
        env.Dialogs.PasswordAnswer = null;

        Assert.Null(await new ResetPasswordAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None));
    }

    [Fact]
    public async Task Prepare_rejects_empty_password_without_echoing_it()
    {
        var env = new TestEnv();
        env.Dialogs.PasswordAnswer = new PasswordChoice("", true);

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new ResetPasswordAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
    }

    [Theory]
    [InlineData("true", true, "сменить при следующем входе: да")]
    [InlineData("false", false, "сменить при следующем входе: нет")]
    public async Task Execute_resets_password_via_directory(string flag, bool expectedMustChange, string expectedAudit)
    {
        var env = new TestEnv();
        var user = Samples.User();
        var ctx = env.With(("password", Secret), ("mustChange", flag));

        var result = await new ResetPasswordAction().ExecuteAsync(Samples.UserTarget(user), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("Пароль сброшен", result.Message);
        Assert.Equal(expectedAudit, ctx.AuditDetails);
        Assert.Equal(new FakeUserDirectory.PasswordReset(user.DistinguishedName, Secret, expectedMustChange), Assert.Single(env.Users.PasswordResets));
    }

    [Fact]
    public async Task Execute_without_flag_requires_change_at_next_logon()
    {
        var env = new TestEnv();

        await new ResetPasswordAction().ExecuteAsync(Samples.UserTarget(), env.With(("password", Secret)), CancellationToken.None);

        Assert.True(Assert.Single(env.Users.PasswordResets).MustChange);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Execute_without_password_fails_and_changes_nothing(string? password)
    {
        var env = new TestEnv();
        var ctx = password is null ? env.Context() : env.With(("password", password));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new ResetPasswordAction().ExecuteAsync(Samples.UserTarget(), ctx, CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        Assert.Empty(env.Users.PasswordResets);
    }

    [Fact]
    public async Task Directory_failure_propagates_unchanged_and_without_password()
    {
        var env = new TestEnv();
        var failure = new OperationFailedException("Пароль не прошёл политику сложности.", OperationFailure.PolicyViolation);
        env.Users.ResetPasswordFailure = failure;
        var ctx = env.With(("password", Secret), ("mustChange", "true"));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new ResetPasswordAction().ExecuteAsync(Samples.UserTarget(), ctx, CancellationToken.None));

        Assert.Same(failure, ex);
        Assert.Null(ctx.AuditDetails);
    }
}

public class GroupActionTests
{
    private static readonly GroupInfo Admins = new("Администраторы печати", "CN=PrintAdmins,OU=Группы,DC=corp,DC=local", "Управление принтерами");
    private static readonly GroupInfo Buh = new("Бухгалтерия", "CN=Buh,OU=Группы,DC=corp,DC=local", "Отдел бухгалтерии");
    private static readonly GroupInfo Vpn = new("VPN Users", "CN=VPN,OU=Группы,DC=corp,DC=local");

    [Fact]
    public void Metadata()
    {
        var add = new AddToGroupAction();
        Assert.Equal(("Добавить в группу…", "users-plus", ActionDanger.Confirm, false), (add.Title, add.Icon, add.Danger, add.SupportsBulk));

        var remove = new RemoveFromGroupAction();
        Assert.Equal(("Удалить из группы…", "users-minus", ActionDanger.ConfirmWithReason, false), (remove.Title, remove.Icon, remove.Danger, remove.SupportsBulk));
    }

    [Fact]
    public async Task Add_prepare_returns_chosen_group()
    {
        var env = new TestEnv();
        env.Dialogs.PickAnswer = Buh;

        var parameters = await new AddToGroupAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None);

        Assert.Equal(Buh.DistinguishedName, parameters!["groupDn"]);
        Assert.Equal("Бухгалтерия", parameters["groupName"]);
        Assert.Equal("Добавить в группу", env.Dialogs.PickTitle);
    }

    [Fact]
    public async Task Add_prepare_search_goes_to_directory_with_limit_30()
    {
        var env = new TestEnv();
        env.Users.FoundGroups.Add(Vpn);

        await new AddToGroupAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None);
        var found = await env.Dialogs.PickSearch!("vpn", CancellationToken.None);

        Assert.Equal([Vpn], found);
        Assert.Equal(("vpn", 30), Assert.Single(env.Users.GroupSearches));
    }

    [Fact]
    public async Task Add_prepare_cancel_returns_null()
    {
        var env = new TestEnv();
        env.Dialogs.PickAnswer = null;

        Assert.Null(await new AddToGroupAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None));
    }

    [Fact]
    public async Task Add_execute_adds_and_audits_group_name()
    {
        var env = new TestEnv();
        var user = Samples.User();
        var ctx = env.With(("groupDn", Buh.DistinguishedName), ("groupName", Buh.Name));

        var result = await new AddToGroupAction().ExecuteAsync(Samples.UserTarget(user), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal((user.DistinguishedName, Buh.DistinguishedName), Assert.Single(env.Users.Added));
        Assert.Contains("Бухгалтерия", ctx.AuditDetails);
        Assert.Contains("Бухгалтерия", result.Message);
    }

    [Fact]
    public async Task Remove_prepare_offers_only_users_groups()
    {
        var env = new TestEnv();
        env.Users.Groups.AddRange([Admins, Buh, Vpn]);
        env.Users.FoundGroups.Add(new GroupInfo("Чужая", "CN=Other,DC=corp,DC=local"));
        env.Dialogs.PickAnswer = Vpn;

        var parameters = await new RemoveFromGroupAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None);

        Assert.Equal(Vpn.DistinguishedName, parameters!["groupDn"]);
        Assert.Equal("VPN Users", parameters["groupName"]);
        Assert.Equal("Удалить из группы", env.Dialogs.PickTitle);
        Assert.Empty(env.Users.GroupSearches);
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("  ", 3)]
    [InlineData("бух", 1)]
    [InlineData("БУХ", 1)]
    [InlineData("vpn", 1)]
    [InlineData("принтер", 1)]
    [InlineData("групп", 0)]
    [InlineData("нет такой", 0)]
    public async Task Remove_prepare_search_filters_user_groups_locally(string text, int expectedCount)
    {
        var env = new TestEnv();
        env.Users.Groups.AddRange([Admins, Buh, Vpn]);

        await new RemoveFromGroupAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None);
        var found = await env.Dialogs.PickSearch!(text, CancellationToken.None);

        Assert.Equal(expectedCount, found.Count);
        Assert.Empty(env.Users.GroupSearches);
    }

    [Fact]
    public async Task Remove_prepare_without_groups_tells_user_and_cancels()
    {
        var env = new TestEnv();

        var parameters = await new RemoveFromGroupAction().PrepareAsync(
            [Samples.UserTarget(Samples.User(displayName: "Петрова А."))], env.Context(), CancellationToken.None);

        Assert.Null(parameters);
        Assert.Contains("Петрова А.", Assert.Single(env.Dialogs.Infos).Message);
        Assert.Null(env.Dialogs.PickSearch);
    }

    [Fact]
    public async Task Remove_prepare_cancel_returns_null()
    {
        var env = new TestEnv();
        env.Users.Groups.Add(Buh);
        env.Dialogs.PickAnswer = null;

        Assert.Null(await new RemoveFromGroupAction().PrepareAsync([Samples.UserTarget()], env.Context(), CancellationToken.None));
    }

    [Fact]
    public async Task Remove_execute_removes_and_audits_group_name()
    {
        var env = new TestEnv();
        var user = Samples.User();
        var ctx = env.With(("groupDn", Vpn.DistinguishedName), ("groupName", Vpn.Name));

        var result = await new RemoveFromGroupAction().ExecuteAsync(Samples.UserTarget(user), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal((user.DistinguishedName, Vpn.DistinguishedName), Assert.Single(env.Users.Removed));
        Assert.Contains("VPN Users", ctx.AuditDetails);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Execute_requires_group(string? groupDn)
    {
        var env = new TestEnv();
        var ctx = groupDn is null ? env.Context() : env.With(("groupDn", groupDn));

        await Assert.ThrowsAsync<OperationFailedException>(() =>
            new AddToGroupAction().ExecuteAsync(Samples.UserTarget(), ctx, CancellationToken.None));
        await Assert.ThrowsAsync<OperationFailedException>(() =>
            new RemoveFromGroupAction().ExecuteAsync(Samples.UserTarget(), ctx, CancellationToken.None));

        Assert.Empty(env.Users.Added);
        Assert.Empty(env.Users.Removed);
    }
}
