using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class PowerActionTests
{
    [Fact]
    public void Metadata()
    {
        var reboot = new RebootAction();
        Assert.Equal("Перезагрузить", reboot.Title);
        Assert.Equal("refresh", reboot.Icon);
        Assert.Equal("Компьютер", reboot.Group);
        Assert.Equal(ActionDanger.ConfirmWithReason, reboot.Danger);
        Assert.True(reboot.SupportsBulk);

        var shutdown = new ShutdownAction();
        Assert.Equal("Выключить", shutdown.Title);
        Assert.Equal("power", shutdown.Icon);
        Assert.Equal("Компьютер", shutdown.Group);
        Assert.Equal(ActionDanger.ConfirmWithReason, shutdown.Danger);
        Assert.True(shutdown.SupportsBulk);
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("1", true, 1)]
    [InlineData("60", true, 60)]
    [InlineData("060", true, 60)]
    [InlineData("3599", true, 3599)]
    [InlineData("3600", true, 3600)]
    [InlineData("3601", false, 0)]
    [InlineData("99999999999", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("+5", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(" 5", false, 0)]
    [InlineData("5 ", false, 0)]
    [InlineData("1.5", false, 0)]
    [InlineData("1,5", false, 0)]
    [InlineData("1e2", false, 0)]
    [InlineData("пять", false, 0)]
    [InlineData("٥", false, 0)]
    public void TryParseDelay_boundaries(string text, bool ok, int expected)
    {
        Assert.Equal(ok, PowerAction.TryParseDelay(text, out var seconds));
        if (ok) Assert.Equal(expected, seconds);
    }

    [Fact]
    public void TryParseDelay_null_is_invalid() => Assert.False(PowerAction.TryParseDelay(null, out _));

    [Theory]
    [InlineData(0, "секунд")]
    [InlineData(1, "секунду")]
    [InlineData(2, "секунды")]
    [InlineData(4, "секунды")]
    [InlineData(5, "секунд")]
    [InlineData(11, "секунд")]
    [InlineData(12, "секунд")]
    [InlineData(14, "секунд")]
    [InlineData(21, "секунду")]
    [InlineData(22, "секунды")]
    [InlineData(60, "секунд")]
    [InlineData(101, "секунду")]
    [InlineData(111, "секунд")]
    [InlineData(3600, "секунд")]
    public void SecondsWord_agrees_with_number(int n, string expected) =>
        Assert.Equal(expected, PowerAction.SecondsWord(n));

    [Fact]
    public void DefaultMessage_texts()
    {
        Assert.Equal("Компьютер будет перезагружен администратором через 60 секунд. Сохраните работу.",
            PowerAction.DefaultMessage(reboot: true, 60));
        Assert.Equal("Компьютер будет выключен администратором через 1 секунду. Сохраните работу.",
            PowerAction.DefaultMessage(reboot: false, 1));
        Assert.Equal("Компьютер будет выключен администратором немедленно. Сохраните работу.",
            PowerAction.DefaultMessage(reboot: false, 0));
    }

    [Fact]
    public async Task Prepare_uses_defaults_and_returns_parameters()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("60");
        env.Dialogs.TextAnswers.Enqueue("Компьютер будет перезагружен администратором через 60 секунд. Сохраните работу.");

        var parameters = await new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Equal("60", parameters!["delaySeconds"]);
        Assert.Equal("Компьютер будет перезагружен администратором через 60 секунд. Сохраните работу.", parameters["message"]);
        Assert.Equal(2, env.Dialogs.TextPrompts.Count);
        Assert.Equal("60", env.Dialogs.TextPrompts[0].Initial);
        Assert.Equal("Компьютер будет перезагружен администратором через 60 секунд. Сохраните работу.",
            env.Dialogs.TextPrompts[1].Initial);
        Assert.Empty(env.Dialogs.Errors);
    }

    [Fact]
    public async Task Prepare_default_message_follows_chosen_delay_and_action()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("120");
        env.Dialogs.TextAnswers.Enqueue("текст");

        await new ShutdownAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Equal("Компьютер будет выключен администратором через 120 секунд. Сохраните работу.",
            env.Dialogs.TextPrompts[1].Initial);
        Assert.Equal("Выключение", env.Dialogs.TextPrompts[0].Title);
    }

    [Fact]
    public async Task Prepare_accepts_delay_boundaries()
    {
        foreach (var delay in new[] { "0", "3600" })
        {
            var env = new TestEnv();
            env.Dialogs.TextAnswers.Enqueue(delay);
            env.Dialogs.TextAnswers.Enqueue("msg");

            var parameters = await new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

            Assert.Equal(delay, parameters!["delaySeconds"]);
            Assert.Empty(env.Dialogs.Errors);
        }
    }

    [Fact]
    public async Task Prepare_asks_again_for_out_of_range_delay()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("3601");
        env.Dialogs.TextAnswers.Enqueue("-5");
        env.Dialogs.TextAnswers.Enqueue("abc");
        env.Dialogs.TextAnswers.Enqueue("30");
        env.Dialogs.TextAnswers.Enqueue("msg");

        var parameters = await new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Equal("30", parameters!["delaySeconds"]);
        Assert.Equal(3, env.Dialogs.Errors.Count);
        Assert.All(env.Dialogs.Errors, e => Assert.Contains("от 0 до 3600", e.Message));
        Assert.Equal("3601", env.Dialogs.TextPrompts[1].Initial);
    }

    [Fact]
    public async Task Prepare_cancel_at_delay_returns_null_and_does_not_ask_message()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue(null);

        var parameters = await new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Null(parameters);
        Assert.Single(env.Dialogs.TextPrompts);
    }

    [Fact]
    public async Task Prepare_cancel_after_invalid_delay_returns_null()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("99999");
        env.Dialogs.TextAnswers.Enqueue(null);

        Assert.Null(await new ShutdownAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None));
        Assert.Single(env.Dialogs.Errors);
    }

    [Fact]
    public async Task Prepare_with_cancelled_token_asks_nothing()
    {
        var env = new TestEnv();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), cts.Token));
        Assert.Empty(env.Dialogs.TextPrompts);
    }

    [Fact]
    public async Task Prepare_cancel_at_message_returns_null()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("10");
        env.Dialogs.TextAnswers.Enqueue(null);

        Assert.Null(await new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None));
    }

    [Fact]
    public async Task Prepare_rejects_too_long_message_and_asks_again()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("10");
        env.Dialogs.TextAnswers.Enqueue(new string('я', PowerAction.MaxMessageLength + 1));
        env.Dialogs.TextAnswers.Enqueue(new string('я', PowerAction.MaxMessageLength));

        var parameters = await new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);

        Assert.Equal(PowerAction.MaxMessageLength, parameters!["message"].Length);
        Assert.Single(env.Dialogs.Errors);
    }

    [Fact]
    public async Task Prepare_mentions_target_or_count()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue(null);
        await new RebootAction().PrepareAsync([Samples.ComputerTarget()], env.Context(), CancellationToken.None);
        Assert.Contains("PC-ACC-014", env.Dialogs.TextPrompts[0].Message);

        env.Dialogs.TextAnswers.Enqueue(null);
        await new RebootAction().PrepareAsync(
            [Samples.ComputerTarget(), Samples.ComputerTarget(Samples.Computer("PC2", "pc2.corp.local"))], env.Context(), CancellationToken.None);
        Assert.Contains("2", env.Dialogs.TextPrompts[1].Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_calls_shutdown_with_delay_and_message(bool reboot)
    {
        var env = new TestEnv();
        IAction<ComputerTarget> action = reboot ? new RebootAction() : new ShutdownAction();
        var ctx = env.With(("delaySeconds", "90"), ("message", "Сохраните работу"));

        var result = await action.ExecuteAsync(Samples.ComputerTarget(), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        var call = Assert.Single(env.Sessions.Shutdowns);
        Assert.Equal(new FakeSessionService.Shutdown(Samples.HostDns, reboot, TimeSpan.FromSeconds(90), "Сохраните работу"), call);
        Assert.Equal(reboot ? "перезагрузка через 90 с" : "выключение через 90 с", ctx.AuditDetails);
        Assert.Contains("90 с", result.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3600")]
    public async Task Execute_accepts_boundary_delays(string delay)
    {
        var env = new TestEnv();

        await new RebootAction().ExecuteAsync(Samples.ComputerTarget(), env.With(("delaySeconds", delay), ("message", "m")), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(int.Parse(delay)), Assert.Single(env.Sessions.Shutdowns).Delay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Execute_passes_null_when_warning_text_is_empty(string message)
    {
        var env = new TestEnv();

        await new ShutdownAction().ExecuteAsync(
            Samples.ComputerTarget(), env.With(("delaySeconds", "5"), ("message", message)), CancellationToken.None);

        Assert.Null(Assert.Single(env.Sessions.Shutdowns).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("abc")]
    public async Task Execute_rejects_missing_or_invalid_delay(string? delay)
    {
        var env = new TestEnv();
        var ctx = delay is null ? env.With(("message", "m")) : env.With(("delaySeconds", delay), ("message", "m"));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new RebootAction().ExecuteAsync(Samples.ComputerTarget(), ctx, CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        Assert.Equal(0, env.Sessions.TotalCalls);
    }
}
