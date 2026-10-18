using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class ShadowActionTests
{
    private const string MstscPath = @"C:\Windows\System32\mstsc.exe";

    private static readonly SessionState[] AllStates = Enum.GetValues<SessionState>();

    [Fact]
    public void View_action_metadata()
    {
        var a = new ShadowViewAction();
        Assert.Equal("Shadow: просмотр", a.Title);
        Assert.Equal("Подключение", a.Group);
        Assert.Equal("eye", a.Icon);
        Assert.Equal(10, a.Order);
        Assert.Equal(ActionDanger.None, a.Danger);
        Assert.False(a.SupportsBulk);
    }

    [Fact]
    public void Control_action_metadata()
    {
        var a = new ShadowControlAction();
        Assert.Equal("Shadow: управление", a.Title);
        Assert.Equal("Подключение", a.Group);
        Assert.Equal("mouse", a.Icon);
        Assert.Equal(20, a.Order);
        Assert.Equal(ActionDanger.None, a.Danger);
        Assert.False(a.SupportsBulk);
    }

    [Theory]
    [InlineData(SessionState.Active, true)]
    [InlineData(SessionState.Disconnected, false)]
    [InlineData(SessionState.Shadow, false)]
    [InlineData(SessionState.Connected, false)]
    [InlineData(SessionState.ConnectQuery, false)]
    [InlineData(SessionState.Init, false)]
    [InlineData(SessionState.Idle, false)]
    [InlineData(SessionState.Listen, false)]
    [InlineData(SessionState.Reset, false)]
    [InlineData(SessionState.Down, false)]
    [InlineData(SessionState.Unknown, false)]
    public void CanExecute_only_for_active_session_with_user(SessionState state, bool expected)
    {
        var env = new TestEnv();
        var target = Samples.SessionTarget(Samples.Session(state: state));

        Assert.Equal(expected, new ShadowViewAction().CanExecute(target, env.Context()));
        Assert.Equal(expected, new ShadowControlAction().CanExecute(target, env.Context()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanExecute_false_without_user_even_if_active(string? user)
    {
        var env = new TestEnv();
        var target = Samples.SessionTarget(Samples.Session(user: user, state: SessionState.Active));

        Assert.False(new ShadowViewAction().CanExecute(target, env.Context()));
        Assert.Equal("В сессии нет пользователя", new ShadowViewAction().GetUnavailableReason(target, env.Context()));
    }

    [Fact]
    public void UnavailableReason_matches_session_for_every_state()
    {
        var env = new TestEnv();
        foreach (var state in AllStates)
        {
            var session = Samples.Session(state: state);
            var target = Samples.SessionTarget(session);

            foreach (IAction<SessionTarget> action in new IAction<SessionTarget>[] { new ShadowViewAction(), new ShadowControlAction() })
            {
                var reason = action.GetUnavailableReason(target, env.Context());
                Assert.Equal(session.ShadowBlockedReason, reason);
                Assert.Equal(reason is null, action.CanExecute(target, env.Context()));
                if (reason is not null) Assert.False(string.IsNullOrWhiteSpace(reason));
            }
        }
    }

    [Theory]
    [InlineData(null, ActionDanger.None)]
    [InlineData("false", ActionDanger.None)]
    [InlineData("no", ActionDanger.None)]
    [InlineData("true", ActionDanger.ConfirmWithReason)]
    [InlineData("True", ActionDanger.ConfirmWithReason)]
    public void GetDanger_requires_reason_only_for_no_consent(string? flag, ActionDanger expected)
    {
        var env = new TestEnv();
        var ctx = flag is null ? env.Context() : env.With((ActionParameters.NoConsent, flag));
        var target = Samples.SessionTarget();

        Assert.Equal(expected, new ShadowViewAction().GetDanger(target, ctx));
        Assert.Equal(expected, new ShadowControlAction().GetDanger(target, ctx));
    }

    [Theory]
    [InlineData(false, false, "/shadow:4")]
    [InlineData(true, false, "/shadow:4 /control")]
    [InlineData(false, true, "/shadow:4 /noConsentPrompt")]
    [InlineData(true, true, "/shadow:4 /control /noConsentPrompt")]
    public async Task Execute_launches_mstsc_with_exact_arguments(bool control, bool noConsent, string tail)
    {
        var env = new TestEnv();
        env.Launcher.Executables["mstsc.exe"] = MstscPath;
        IAction<SessionTarget> action = control ? new ShadowControlAction() : new ShadowViewAction();
        var ctx = noConsent ? env.With((ActionParameters.NoConsent, "true")) : env.Context();

        var result = await action.ExecuteAsync(Samples.SessionTarget(Samples.Session(id: 4)), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        var launch = Assert.Single(env.Launcher.Launches);
        Assert.Equal(MstscPath, launch.FileName);
        Assert.Equal(("/v:" + Samples.HostDns + " " + tail).Split(' '), launch.Arguments);
    }

    [Fact]
    public async Task Execute_falls_back_to_plain_name_when_mstsc_not_found()
    {
        var env = new TestEnv();

        await new ShadowViewAction().ExecuteAsync(Samples.SessionTarget(), env.Context(), CancellationToken.None);

        Assert.Equal("mstsc.exe", Assert.Single(env.Launcher.Launches).FileName);
    }

    [Fact]
    public async Task Execute_uses_short_name_when_no_dns_name()
    {
        var env = new TestEnv();
        var target = Samples.SessionTarget(computer: Samples.Computer("PC1", dnsName: null));

        await new ShadowControlAction().ExecuteAsync(target, env.Context(), CancellationToken.None);

        Assert.Equal("/v:PC1", Assert.Single(env.Launcher.Launches).Arguments[0]);
    }

    [Theory]
    [InlineData(false, false, "shadow: просмотр, без запроса пользователю: нет")]
    [InlineData(true, false, "shadow: управление, без запроса пользователю: нет")]
    [InlineData(false, true, "shadow: просмотр, без запроса пользователю: да")]
    [InlineData(true, true, "shadow: управление, без запроса пользователю: да")]
    public async Task Execute_sets_audit_details(bool control, bool noConsent, string expected)
    {
        var env = new TestEnv();
        IAction<SessionTarget> action = control ? new ShadowControlAction() : new ShadowViewAction();
        var ctx = noConsent ? env.With((ActionParameters.NoConsent, "true")) : env.Context();

        await action.ExecuteAsync(Samples.SessionTarget(), ctx, CancellationToken.None);

        Assert.Equal(expected, ctx.AuditDetails);
    }

    [Fact]
    public async Task Execute_with_consent_message_has_no_policy_warning()
    {
        var env = new TestEnv();

        var result = await new ShadowViewAction().ExecuteAsync(Samples.SessionTarget(), env.Context(), CancellationToken.None);

        Assert.Equal("Подключение запущено. Если политика требует согласия, пользователь увидит запрос.", result.Message);
    }

    [Fact]
    public async Task Execute_without_consent_warns_about_shadow_policy()
    {
        var env = new TestEnv();

        var view = await new ShadowViewAction().ExecuteAsync(
            Samples.SessionTarget(), env.With((ActionParameters.NoConsent, "true")), CancellationToken.None);
        var control = await new ShadowControlAction().ExecuteAsync(
            Samples.SessionTarget(), env.With((ActionParameters.NoConsent, "true")), CancellationToken.None);

        Assert.StartsWith("Подключение запущено.", view.Message);
        Assert.Contains("политика Shadow", view.Message);
        Assert.Contains("2 или 4", view.Message);
        Assert.Contains("mstsc вернёт ошибку", view.Message);
        Assert.Contains("диагностика", view.Message);

        Assert.Contains("политика Shadow", control.Message);
        Assert.DoesNotContain("2 или 4", control.Message);
    }

    [Fact]
    public async Task Execute_rejects_bad_session_id_without_launching()
    {
        var env = new TestEnv();
        var target = Samples.SessionTarget(Samples.Session(id: -1));

        await Assert.ThrowsAsync<OperationFailedException>(() =>
            new ShadowViewAction().ExecuteAsync(target, env.Context(), CancellationToken.None));
        Assert.Empty(env.Launcher.Launches);
    }
}
