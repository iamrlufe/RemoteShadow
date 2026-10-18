using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class RdpActionTests
{
    [Fact]
    public void Metadata()
    {
        var a = new RdpAction();
        Assert.Equal("RDP", a.Title);
        Assert.Equal("Подключение", a.Group);
        Assert.Equal("device-desktop", a.Icon);
        Assert.Equal(30, a.Order);
        Assert.Equal(ActionDanger.None, a.Danger);
        Assert.False(a.SupportsBulk);
    }

    [Fact]
    public void CanExecute_is_always_true()
    {
        var env = new TestEnv();
        var offline = Samples.Computer() with { IsOnline = false };

        Assert.True(new RdpAction().CanExecute(Samples.ComputerTarget(offline), env.Context()));
        Assert.True(new RdpAction().CanExecute(Samples.ComputerTarget(Samples.Computer() with { Enabled = false }), env.Context()));
    }

    [Fact]
    public async Task Execute_launches_mstsc_with_host_only()
    {
        var env = new TestEnv();
        env.Launcher.Executables["mstsc.exe"] = @"C:\Windows\System32\mstsc.exe";

        var result = await new RdpAction().ExecuteAsync(Samples.ComputerTarget(), env.Context(), CancellationToken.None);

        Assert.True(result.IsOk);
        var launch = Assert.Single(env.Launcher.Launches);
        Assert.Equal(@"C:\Windows\System32\mstsc.exe", launch.FileName);
        Assert.Equal(["/v:" + Samples.HostDns], launch.Arguments);
    }

    [Fact]
    public async Task Execute_falls_back_to_plain_name()
    {
        var env = new TestEnv();

        await new RdpAction().ExecuteAsync(Samples.ComputerTarget(), env.Context(), CancellationToken.None);

        Assert.Equal("mstsc.exe", Assert.Single(env.Launcher.Launches).FileName);
    }
}

public class OpenAdminShareActionTests
{
    [Fact]
    public void Metadata()
    {
        var a = new OpenAdminShareAction();
        Assert.Equal("Открыть C$", a.Title);
        Assert.Equal("Подключение", a.Group);
        Assert.Equal("folder", a.Icon);
        Assert.Equal(50, a.Order);
        Assert.Equal(ActionDanger.None, a.Danger);
        Assert.False(a.SupportsBulk);
        Assert.True(a.CanExecute(Samples.ComputerTarget(), new TestEnv().Context()));
    }

    [Fact]
    public async Task Execute_opens_unc_path_in_explorer()
    {
        var env = new TestEnv();
        env.Launcher.Executables["explorer.exe"] = @"C:\Windows\explorer.exe";

        await new OpenAdminShareAction().ExecuteAsync(Samples.ComputerTarget(), env.Context(), CancellationToken.None);

        var launch = Assert.Single(env.Launcher.Launches);
        Assert.Equal(@"C:\Windows\explorer.exe", launch.FileName);
        Assert.Equal([@"\\" + Samples.HostDns + @"\C$"], launch.Arguments);
    }

    [Fact]
    public async Task Execute_falls_back_to_plain_name()
    {
        var env = new TestEnv();

        await new OpenAdminShareAction().ExecuteAsync(Samples.ComputerTarget(), env.Context(), CancellationToken.None);

        Assert.Equal("explorer.exe", Assert.Single(env.Launcher.Launches).FileName);
    }
}

public class SshCommandTests
{
    [Theory]
    [InlineData("user")]
    [InlineData("adm-ivanov")]
    [InlineData("a.b_c-d")]
    [InlineData("A1")]
    [InlineData("1")]
    [InlineData("_svc")]
    [InlineData(".hidden")]
    public void Valid_logins(string login) => Assert.True(SshCommand.IsValidLogin(login));

    [Fact]
    public void Login_length_boundaries()
    {
        Assert.True(SshCommand.IsValidLogin(new string('a', 64)));
        Assert.False(SshCommand.IsValidLogin(new string('a', 65)));
        Assert.False(SshCommand.IsValidLogin(""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" user")]
    [InlineData("user ")]
    [InlineData("us er")]
    [InlineData("user@corp.local")]
    [InlineData("user@")]
    [InlineData(@"CORP\user")]
    [InlineData("-v")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("-")]
    [InlineData("us;er")]
    [InlineData("us&er")]
    [InlineData("us|er")]
    [InlineData("us\"er")]
    [InlineData("us'er")]
    [InlineData("us=er")]
    [InlineData("user\n")]
    [InlineData("user\r\n")]
    [InlineData("\tuser")]
    [InlineData("юзер")]
    [InlineData("us/er")]
    public void Invalid_logins(string? login) => Assert.False(SshCommand.IsValidLogin(login));

    [Theory]
    [InlineData(@"CORP\adm-ivanov", "adm-ivanov")]
    [InlineData("adm-ivanov@corp.local", "adm-ivanov")]
    [InlineData("adm-ivanov", "adm-ivanov")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void DefaultLogin_drops_domain(string? account, string expected) =>
        Assert.Equal(expected, SshCommand.DefaultLogin(account));

    [Fact]
    public void Destination_joins_login_and_host()
    {
        Assert.Equal("adm@pc1.corp.local", SshCommand.Destination("adm", "pc1.corp.local"));
    }

    [Fact]
    public void Destination_rejects_bad_login_or_host()
    {
        Assert.Throws<OperationFailedException>(() => SshCommand.Destination("-v", "pc1"));
        Assert.Throws<OperationFailedException>(() => SshCommand.Destination("adm", "-evil"));
        Assert.Throws<OperationFailedException>(() => SshCommand.Destination("adm", "pc1\n"));
    }
}

public class SshActionTests
{
    private const string SshPath = @"C:\Windows\System32\OpenSSH\ssh.exe";
    private const string WtPath = @"C:\Users\adm\AppData\Local\Microsoft\WindowsApps\wt.exe";

    [Fact]
    public void Metadata()
    {
        var a = new SshAction();
        Assert.Equal("SSH", a.Title);
        Assert.Equal("Подключение", a.Group);
        Assert.Equal("terminal-2", a.Icon);
        Assert.Equal(40, a.Order);
        Assert.Equal(ActionDanger.None, a.Danger);
        Assert.False(a.SupportsBulk);
        Assert.True(a.CanExecute(Samples.ComputerTarget(), new TestEnv().Context()));
    }

    [Fact]
    public async Task Prepare_offers_account_name_without_domain_and_stores_login()
    {
        var env = new TestEnv(@"CORP\adm-ivanov");
        env.Dialogs.TextAnswers.Enqueue("  root  ");

        var parameters = await new SshAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.NotNull(parameters);
        Assert.Equal("root", parameters["sshUser"]);
        var prompt = Assert.Single(env.Dialogs.TextPrompts);
        Assert.Equal("adm-ivanov", prompt.Initial);
        Assert.False(prompt.Multiline);
        Assert.Contains("PC-ACC-014", prompt.Message);
        Assert.Empty(env.Dialogs.Errors);
    }

    [Fact]
    public async Task Prepare_default_from_upn_account()
    {
        var env = new TestEnv("adm-ivanov@corp.local");

        await new SshAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Equal("adm-ivanov", Assert.Single(env.Dialogs.TextPrompts).Initial);
    }

    [Fact]
    public async Task Prepare_cancel_returns_null()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue(null);

        var parameters = await new SshAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Null(parameters);
        Assert.Empty(env.Dialogs.Errors);
    }

    [Fact]
    public async Task Prepare_with_cancelled_token_asks_nothing()
    {
        var env = new TestEnv();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new SshAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), cts.Token));
        Assert.Empty(env.Dialogs.TextPrompts);
    }

    [Fact]
    public async Task Prepare_asks_again_after_invalid_login()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("user@corp.local");
        env.Dialogs.TextAnswers.Enqueue("-oProxyCommand=calc");
        env.Dialogs.TextAnswers.Enqueue("user");

        var parameters = await new SshAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Equal("user", parameters!["sshUser"]);
        Assert.Equal(2, env.Dialogs.Errors.Count);
        Assert.Equal(3, env.Dialogs.TextPrompts.Count);
        Assert.Equal("user@corp.local", env.Dialogs.TextPrompts[1].Initial);
    }

    [Fact]
    public async Task Prepare_cancel_after_invalid_login_returns_null()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("bad login");
        env.Dialogs.TextAnswers.Enqueue(null);

        var parameters = await new SshAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Null(parameters);
        Assert.Single(env.Dialogs.Errors);
    }

    [Fact]
    public async Task Execute_uses_windows_terminal_when_found()
    {
        var env = new TestEnv();
        env.Launcher.Executables["ssh.exe"] = SshPath;
        env.Launcher.Executables["wt.exe"] = WtPath;
        var ctx = env.With(("sshUser", "adm"));

        var result = await new SshAction().ExecuteAsync(Samples.ComputerTarget(), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        var launch = Assert.Single(env.Launcher.Launches);
        Assert.Equal(WtPath, launch.FileName);
        Assert.Equal(["ssh", "adm@" + Samples.HostDns], launch.Arguments);
        Assert.Equal("ssh: adm@" + Samples.HostDns + ", Windows Terminal", ctx.AuditDetails);
    }

    [Fact]
    public async Task Execute_uses_ssh_exe_without_windows_terminal()
    {
        var env = new TestEnv();
        env.Launcher.Executables["ssh.exe"] = SshPath;
        var ctx = env.With(("sshUser", "adm"));

        await new SshAction().ExecuteAsync(Samples.ComputerTarget(), ctx, CancellationToken.None);

        var launch = Assert.Single(env.Launcher.Launches);
        Assert.Equal(SshPath, launch.FileName);
        Assert.Equal(["adm@" + Samples.HostDns], launch.Arguments);
        Assert.Equal("ssh: adm@" + Samples.HostDns + ", консоль", ctx.AuditDetails);
    }

    [Fact]
    public async Task Execute_without_ssh_exe_explains_how_to_enable_openssh()
    {
        var env = new TestEnv();
        var ctx = env.With(("sshUser", "adm"));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new SshAction().ExecuteAsync(Samples.ComputerTarget(), ctx, CancellationToken.None));

        Assert.Contains("«Клиент OpenSSH»", ex.Message);
        Assert.Contains("ssh.exe", ex.Message);
        Assert.Equal(OperationFailure.NotFound, ex.Failure);
        Assert.Empty(env.Launcher.Launches);
    }

    [Fact]
    public async Task Execute_with_terminal_but_without_ssh_does_not_open_empty_tab()
    {
        var env = new TestEnv();
        env.Launcher.Executables["wt.exe"] = WtPath;
        var ctx = env.With(("sshUser", "adm"));

        await Assert.ThrowsAsync<OperationFailedException>(() =>
            new SshAction().ExecuteAsync(Samples.ComputerTarget(), ctx, CancellationToken.None));

        Assert.Empty(env.Launcher.Launches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("user@corp.local")]
    [InlineData("us er")]
    public async Task Execute_rejects_missing_or_invalid_login(string? login)
    {
        var env = new TestEnv();
        env.Launcher.Executables["ssh.exe"] = SshPath;
        var ctx = login is null ? env.Context() : env.With(("sshUser", login));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new SshAction().ExecuteAsync(Samples.ComputerTarget(), ctx, CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        Assert.Empty(env.Launcher.Launches);
    }
}
