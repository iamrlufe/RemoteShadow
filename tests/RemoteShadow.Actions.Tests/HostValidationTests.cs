using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

/// <summary>Каждое действие, передающее имя компьютера наружу, обязано отказать на недопустимом имени и ничего не сделать.</summary>
public class HostValidationTests
{
    [Theory]
    [MemberData(nameof(TestData.BadHostPerAction), MemberType = typeof(TestData))]
    public async Task Action_rejects_bad_host_and_does_nothing(string action, string badHost)
    {
        var env = new TestEnv();
        // Всё «найдено», чтобы отказ был именно из-за имени, а не из-за отсутствия программ.
        env.Launcher.Executables["mstsc.exe"] = @"C:\Windows\System32\mstsc.exe";
        env.Launcher.Executables["ssh.exe"] = @"C:\Windows\System32\OpenSSH\ssh.exe";
        env.Launcher.Executables["wt.exe"] = @"C:\wt.exe";
        env.Launcher.Executables["explorer.exe"] = @"C:\Windows\explorer.exe";
        var computer = Samples.Computer(name: badHost, dnsName: badHost);

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() => Run(action, env, computer));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        Assert.Empty(env.Launcher.Launches);
        Assert.Equal(0, env.Sessions.TotalCalls);
        Assert.Empty(env.Diagnostics.Fixes);
    }

    [Theory]
    [InlineData("shadow-view")]
    [InlineData("shadow-control")]
    [InlineData("rdp")]
    [InlineData("ssh")]
    [InlineData("open-admin-share")]
    [InlineData("send-message")]
    [InlineData("disconnect")]
    [InlineData("logoff")]
    [InlineData("reboot")]
    [InlineData("shutdown")]
    [InlineData("apply-fix")]
    public async Task Action_accepts_normal_host(string action)
    {
        var env = new TestEnv();
        env.Launcher.Executables["ssh.exe"] = @"C:\Windows\System32\OpenSSH\ssh.exe";

        await Run(action, env, Samples.Computer());

        var calls = env.Launcher.Launches.Count + env.Sessions.TotalCalls + env.Diagnostics.Fixes.Count;
        Assert.Equal(1, calls);
    }

    private static Task Run(string action, TestEnv env, ComputerInfo computer)
    {
        var ct = CancellationToken.None;
        var session = Samples.Session();
        var fixItem = new DiagnosticItem("termservice", "TermService", DiagnosticStatus.Fail, "d", FixId: "start-termservice");

        return action switch
        {
            "shadow-view" => new ShadowViewAction().ExecuteAsync(new SessionTarget(computer, session), env.Context(), ct),
            "shadow-control" => new ShadowControlAction().ExecuteAsync(new SessionTarget(computer, session), env.Context(), ct),
            "shadow-view-noconsent" => new ShadowViewAction().ExecuteAsync(
                new SessionTarget(computer, session), env.With((ActionParameters.NoConsent, "true")), ct),
            "rdp" => new RdpAction().ExecuteAsync(new ComputerTarget(computer), env.Context(), ct),
            "ssh" => new SshAction().ExecuteAsync(new ComputerTarget(computer), env.With(("sshUser", "adm")), ct),
            "open-admin-share" => new OpenAdminShareAction().ExecuteAsync(new ComputerTarget(computer), env.Context(), ct),
            "send-message" => new SendMessageAction().ExecuteAsync(new SessionTarget(computer, session), env.With(("message", "hi")), ct),
            "disconnect" => new DisconnectSessionAction().ExecuteAsync(new SessionTarget(computer, session), env.Context(), ct),
            "logoff" => new LogoffSessionAction().ExecuteAsync(new SessionTarget(computer, session), env.Context(), ct),
            "reboot" => new RebootAction().ExecuteAsync(
                new ComputerTarget(computer), env.With(("delaySeconds", "60"), ("message", "m")), ct),
            "shutdown" => new ShutdownAction().ExecuteAsync(
                new ComputerTarget(computer), env.With(("delaySeconds", "60"), ("message", "m")), ct),
            "apply-fix" => new ApplyDiagnosticFixAction().ExecuteAsync(new DiagnosticFixTarget(computer, fixItem), env.Context(), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }
}
