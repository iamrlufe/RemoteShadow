using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class SessionActionTests
{
    private static readonly SessionState[] AllStates = Enum.GetValues<SessionState>();

    private static IEnumerable<IAction<SessionTarget>> AllActions() =>
        [new SendMessageAction(), new DisconnectSessionAction(), new LogoffSessionAction()];

    [Fact]
    public void Metadata()
    {
        var send = new SendMessageAction();
        Assert.Equal("Сообщение", send.Title);
        Assert.Equal("message", send.Icon);
        Assert.Equal(ActionDanger.None, send.Danger);
        Assert.True(send.SupportsBulk);

        var disconnect = new DisconnectSessionAction();
        Assert.Equal("Отключить", disconnect.Title);
        Assert.Equal("plug-x", disconnect.Icon);
        Assert.Equal(ActionDanger.Confirm, disconnect.Danger);
        Assert.True(disconnect.SupportsBulk);

        var logoff = new LogoffSessionAction();
        Assert.Equal("Выйти из системы", logoff.Title);
        Assert.Equal("logout", logoff.Icon);
        Assert.Equal(ActionDanger.ConfirmWithReason, logoff.Danger);
        Assert.True(logoff.SupportsBulk);

        Assert.All(AllActions(), a => Assert.Equal("Сессия", a.Group));
    }

    [Fact]
    public void CanExecute_follows_HasUser_for_every_state()
    {
        var env = new TestEnv();
        foreach (var state in AllStates)
        {
            var withUser = Samples.SessionTarget(Samples.Session(state: state));
            var withoutUser = Samples.SessionTarget(Samples.Session(user: null, state: state));

            foreach (var action in AllActions())
            {
                Assert.True(action.CanExecute(withUser, env.Context()));
                Assert.Null(action.GetUnavailableReason(withUser, env.Context()));
                Assert.False(action.CanExecute(withoutUser, env.Context()));
                Assert.Equal("В сессии нет пользователя", action.GetUnavailableReason(withoutUser, env.Context()));
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CanExecute_false_for_blank_user_name(string user)
    {
        var target = Samples.SessionTarget(Samples.Session(user: user));
        Assert.All(AllActions(), a => Assert.False(a.CanExecute(target, new TestEnv().Context())));
    }

    [Fact]
    public async Task Send_message_prepare_asks_multiline_and_returns_trimmed_text()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("  Сохраните работу\nчерез 5 минут  ");

        var parameters = await new SendMessageAction().PrepareAsync([Samples.SessionTarget()], env.Context(), CancellationToken.None);

        Assert.Equal("Сохраните работу\nчерез 5 минут", parameters!["message"]);
        Assert.True(Assert.Single(env.Dialogs.TextPrompts).Multiline);
    }

    [Fact]
    public async Task Send_message_prepare_mentions_count_for_batch()
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue("текст");
        var targets = new[] { Samples.SessionTarget(), Samples.SessionTarget(Samples.Session(id: 2)) };

        await new SendMessageAction().PrepareAsync(targets, env.Context(), CancellationToken.None);

        Assert.Contains("2", Assert.Single(env.Dialogs.TextPrompts).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t ")]
    public async Task Send_message_prepare_cancels_on_empty_or_cancelled_text(string? answer)
    {
        var env = new TestEnv();
        env.Dialogs.TextAnswers.Enqueue(answer);

        var parameters = await new SendMessageAction().PrepareAsync([Samples.SessionTarget()], env.Context(), CancellationToken.None);

        Assert.Null(parameters);
    }

    [Fact]
    public async Task Send_message_execute_sends_with_fixed_title()
    {
        var env = new TestEnv();
        var ctx = env.With(("message", "Привет"));

        var result = await new SendMessageAction().ExecuteAsync(Samples.SessionTarget(Samples.Session(id: 7)), ctx, CancellationToken.None);

        Assert.True(result.IsOk);
        var sent = Assert.Single(env.Sessions.Messages);
        Assert.Equal(new FakeSessionService.Message(Samples.HostDns, 7, "Сообщение от администратора", "Привет"), sent);
    }

    [Fact]
    public async Task Send_message_audit_has_length_and_preview()
    {
        var env = new TestEnv();
        var ctx = env.With(("message", "Привет"));

        await new SendMessageAction().ExecuteAsync(Samples.SessionTarget(), ctx, CancellationToken.None);

        Assert.Equal("сообщение: 6 симв., начало: «Привет»", ctx.AuditDetails);
    }

    [Fact]
    public async Task Send_message_audit_preview_is_first_80_chars_with_ellipsis()
    {
        var env = new TestEnv();
        var text = new string('а', 79) + "бВГД";
        var ctx = env.With(("message", text));

        await new SendMessageAction().ExecuteAsync(Samples.SessionTarget(), ctx, CancellationToken.None);

        var expectedPreview = new string('а', 79) + "б…";
        Assert.Equal($"сообщение: {text.Length} симв., начало: «{expectedPreview}»", ctx.AuditDetails);
        Assert.Equal(text, Assert.Single(env.Sessions.Messages).Text);
    }

    [Fact]
    public async Task Send_message_audit_preview_of_exactly_80_chars_has_no_ellipsis()
    {
        var env = new TestEnv();
        var text = new string('x', 80);
        var ctx = env.With(("message", text));

        await new SendMessageAction().ExecuteAsync(Samples.SessionTarget(), ctx, CancellationToken.None);

        Assert.Equal($"сообщение: 80 симв., начало: «{text}»", ctx.AuditDetails);
    }

    [Fact]
    public void Preview_collapses_line_breaks_and_keeps_surrogate_pairs()
    {
        Assert.Equal("строка 1 строка 2", SendMessageAction.Preview("строка 1\r\nстрока 2"));

        var withEmoji = new string('a', 79) + "\U0001F600" + "tail";
        var preview = SendMessageAction.Preview(withEmoji);
        Assert.Equal(new string('a', 79) + "…", preview);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Send_message_execute_requires_text(string? text)
    {
        var env = new TestEnv();
        var ctx = text is null ? env.Context() : env.With(("message", text));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(() =>
            new SendMessageAction().ExecuteAsync(Samples.SessionTarget(), ctx, CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        Assert.Equal(0, env.Sessions.TotalCalls);
    }

    [Fact]
    public async Task Disconnect_calls_service()
    {
        var env = new TestEnv();

        var result = await new DisconnectSessionAction().ExecuteAsync(
            Samples.SessionTarget(Samples.Session(id: 3)), env.Context(), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal((Samples.HostDns, 3), Assert.Single(env.Sessions.Disconnects));
        Assert.Equal(1, env.Sessions.TotalCalls);
    }

    [Fact]
    public async Task Logoff_calls_service()
    {
        var env = new TestEnv();

        var result = await new LogoffSessionAction().ExecuteAsync(
            Samples.SessionTarget(Samples.Session(id: 5)), env.Context(), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal((Samples.HostDns, 5), Assert.Single(env.Sessions.Logoffs));
        Assert.Equal(1, env.Sessions.TotalCalls);
    }
}
