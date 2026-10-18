using System.Text.Json;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

/// <summary>Настраиваемое действие для проверки <see cref="ActionRunner"/>.</summary>
public sealed class TestAction<TTarget> : IAction<TTarget> where TTarget : IActionTarget
{
    private readonly object _gate = new();
    private readonly List<(TTarget Target, ActionContext Context)> _executions = new();
    private int _running;
    private int _maxRunning;
    private int _prepareCalls;

    public string Id { get; init; } = "test.action";
    public string Title { get; init; } = "Тестовое действие";
    public string Group { get; init; } = "Тест";
    public string? Icon => null;
    public ActionDanger Danger { get; init; } = ActionDanger.None;
    public bool SupportsBulk { get; init; }
    public int Order { get; init; }

    public Func<TTarget, ActionContext, bool> CanExecuteFunc { get; set; } = (_, _) => true;
    public Func<TTarget, ActionContext, string?>? UnavailableReasonFunc { get; set; }
    public Func<TTarget, ActionContext, ActionDanger>? DangerFunc { get; set; }

    public Func<IReadOnlyList<TTarget>, ActionContext, CancellationToken, Task<IReadOnlyDictionary<string, string>?>>? PrepareFunc { get; set; }

    public Func<TTarget, ActionContext, CancellationToken, Task<ActionResult>> ExecuteFunc { get; set; } =
        (_, _, _) => Task.FromResult(ActionResult.Ok());

    public IReadOnlyList<(TTarget Target, ActionContext Context)> Executions
    {
        get
        {
            lock (_gate) return _executions.ToList();
        }
    }

    public int MaxRunning => Volatile.Read(ref _maxRunning);
    public int Running => Volatile.Read(ref _running);
    public int PrepareCalls => Volatile.Read(ref _prepareCalls);
    public List<IReadOnlyList<TTarget>> PreparedWith { get; } = new();

    public bool CanExecute(TTarget target, ActionContext context) => CanExecuteFunc(target, context);

    public string? GetUnavailableReason(TTarget target, ActionContext context) => UnavailableReasonFunc?.Invoke(target, context);

    public ActionDanger GetDanger(TTarget target, ActionContext context) => DangerFunc?.Invoke(target, context) ?? Danger;

    public async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(IReadOnlyList<TTarget> targets, ActionContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _prepareCalls);
        lock (PreparedWith) PreparedWith.Add(targets);
        if (PrepareFunc is null) return new Dictionary<string, string>();
        return await PrepareFunc(targets, context, ct);
    }

    public async Task<ActionResult> ExecuteAsync(TTarget target, ActionContext context, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _running);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxRunning)))
        {
            if (Interlocked.CompareExchange(ref _maxRunning, now, seen) == seen) break;
        }

        try
        {
            lock (_gate) _executions.Add((target, context));
            return await ExecuteFunc(target, context, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

public class ActionRunnerTests
{
    private sealed class Fixture
    {
        public Fixture(IDomainConnection? connection = null)
        {
            Connection = connection is null ? FakeCurrentConnection.LoggedIn() : new FakeCurrentConnection(connection);
            Provider = new SimpleServiceProvider().Add<IDialogService>(Dialogs);
            Runner = new ActionRunner(Provider, Audit, Settings, Dialogs, Connection);
        }

        public FakeAuditLog Audit { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public FakeCurrentConnection Connection { get; }
        public SimpleServiceProvider Provider { get; }
        public ActionRunner Runner { get; }

        public Task<BulkResult> RunAsync(
            TestAction<ComputerTarget> action,
            IReadOnlyList<ComputerTarget> targets,
            IReadOnlyDictionary<string, string>? parameters = null,
            IProgress<BulkProgress>? progress = null,
            CancellationToken ct = default) =>
            Runner.RunAsync(action, targets, parameters, progress, ct);
    }

    private static ComputerTarget Target(string name, int id = 0) => new(Pc(name, id: id == 0 ? name.GetHashCode() & 0x7FFFFFFF : id));

    private static List<ComputerTarget> Targets(int count) =>
        Enumerable.Range(1, count).Select(i => Target($"PC-{i:00}", i)).ToList();

    private static string[] Labels(IEnumerable<ComputerTarget> targets) => targets.Select(t => t.Label).ToArray();

    // ----- GetActions / CreateContext -----------------------------------------------------------------------------------

    [Fact]
    public void GetActions_sorts_by_group_then_order_then_title()
    {
        var fx = new Fixture();
        var a = new TestAction<ComputerTarget> { Id = "a", Group = "Сессия", Title = "Б", Order = 0 };
        var b = new TestAction<ComputerTarget> { Id = "b", Group = "Подключение", Title = "Я", Order = 2 };
        var c = new TestAction<ComputerTarget> { Id = "c", Group = "Подключение", Title = "Ё-второе", Order = 1 };
        var d = new TestAction<ComputerTarget> { Id = "d", Group = "Подключение", Title = "Бета", Order = 1 };
        var e = new TestAction<ComputerTarget> { Id = "e", Group = "Файлы", Title = "А", Order = -5 };
        fx.Provider.AddActions<ComputerTarget>(a, b, c, d, e);

        var sorted = fx.Runner.GetActions<ComputerTarget>();

        // «Подключение»: порядок 1 (Бета, Ё-второе), потом порядок 2 (Я); затем «Сессия», «Файлы».
        Assert.Equal(new[] { "d", "c", "b", "a", "e" }, sorted.Select(x => x.Id));
    }

    [Fact]
    public void GetActions_ignores_letter_case_when_sorting_titles()
    {
        var fx = new Fixture();
        fx.Provider.AddActions<ComputerTarget>(
            new TestAction<ComputerTarget> { Id = "2", Title = "бета" },
            new TestAction<ComputerTarget> { Id = "1", Title = "Альфа" },
            new TestAction<ComputerTarget> { Id = "3", Title = "Гамма" });

        Assert.Equal(new[] { "1", "2", "3" }, fx.Runner.GetActions<ComputerTarget>().Select(x => x.Id));
    }

    [Fact]
    public void GetActions_is_stable_and_separate_for_each_target_type()
    {
        var fx = new Fixture();
        fx.Provider
            .AddActions<ComputerTarget>(new TestAction<ComputerTarget> { Id = "pc" })
            .AddActions<UserTarget>(new TestAction<UserTarget> { Id = "user1" }, new TestAction<UserTarget> { Id = "user2", Order = 1 });

        Assert.Same(fx.Runner.GetActions<ComputerTarget>(), fx.Runner.GetActions<ComputerTarget>());
        Assert.Equal(new[] { "pc" }, fx.Runner.GetActions<ComputerTarget>().Select(x => x.Id));
        Assert.Equal(new[] { "user1", "user2" }, fx.Runner.GetActions<UserTarget>().Select(x => x.Id));
    }

    [Fact]
    public void GetActions_without_registered_actions_is_empty()
    {
        var fx = new Fixture();

        Assert.Empty(fx.Runner.GetActions<SessionTarget>());
    }

    [Fact]
    public void CreateContext_carries_services_and_parameters()
    {
        var fx = new Fixture();

        var context = fx.Runner.CreateContext(new Dictionary<string, string> { [ActionParameters.NoConsent] = "true" });
        var empty = fx.Runner.CreateContext();

        Assert.Same(fx.Provider, context.Services);
        Assert.True(context.GetFlag(ActionParameters.NoConsent));
        Assert.Empty(empty.Parameters);
        Assert.Null(context.Reason);
        Assert.Same(fx.Dialogs, context.Dialogs);
    }

    // ----- простое выполнение и журнал --------------------------------------------------------------------------------------

    [Fact]
    public async Task Safe_action_runs_without_dialogs_and_is_audited_with_all_fields()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            Id = "computer.ping",
            Title = "Проверить связь",
            ExecuteFunc = (_, ctx, _) =>
            {
                ctx.AuditDetails = "порт 445";
                return Task.FromResult(ActionResult.Ok("отвечает"));
            },
        };

        var result = await fx.RunAsync(action, new[] { Target("PC-001", 1) });

        Assert.False(result.Cancelled);
        Assert.True(result.AllOk);
        Assert.Equal("PC-001", Assert.Single(result.Results).Target);
        Assert.Equal("отвечает", result.Summary);
        Assert.Empty(fx.Dialogs.ConfirmRequests);
        Assert.Empty(fx.Dialogs.ReasonRequests);

        var record = Assert.Single(fx.Audit.Records);
        Assert.Equal(@"CORP\adm-ivanov", record.Account);
        Assert.Equal("computer.ping", record.ActionId);
        Assert.Equal("Проверить связь", record.ActionTitle);
        Assert.Equal("computer", record.TargetKind);
        Assert.Equal("PC-001", record.Target);
        Assert.Null(record.Reason);
        Assert.Equal(ActionStatus.Ok, record.Status);
        Assert.Equal("отвечает", record.Message);
        Assert.Equal("порт 445", record.Details);
        Assert.Equal(DateTimeKind.Utc, record.TimeUtc.Kind);
    }

    [Fact]
    public async Task Account_falls_back_to_windows_user_when_not_connected()
    {
        var fx = new Fixture();
        fx.Connection.Set(null);

        await fx.RunAsync(new TestAction<ComputerTarget>(), new[] { Target("PC-1", 1) });

        Assert.Equal($@"{Environment.UserDomainName}\{Environment.UserName}", fx.Audit.Records.Single().Account);
    }

    [Fact]
    public async Task Session_and_user_targets_are_audited_with_their_kind_and_label()
    {
        var fx = new Fixture();
        var runner = fx.Runner;
        var sessionTarget = new SessionTarget(Pc("PC-1", id: 1), Session(3, "ivanov"));
        var userTarget = new UserTarget(User("ivanov", "Иванов Иван"));

        await runner.RunAsync(new TestAction<SessionTarget> { Id = "session.logoff" }, new[] { sessionTarget }, null, null, default);
        await runner.RunAsync(new TestAction<UserTarget> { Id = "user.unlock" }, new[] { userTarget }, null, null, default);

        var records = fx.Audit.Records;
        Assert.Equal(("session", "PC-1 · сессия 3 (ivanov)"), (records[0].TargetKind, records[0].Target));
        Assert.Equal(("user", "Иванов Иван (ivanov)"), (records[1].TargetKind, records[1].Target));
    }

    [Fact]
    public async Task Empty_target_list_does_nothing()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>();

        var result = await fx.RunAsync(action, Array.Empty<ComputerTarget>());

        Assert.Empty(result.Results);
        Assert.False(result.Cancelled);
        Assert.Equal(0, action.PrepareCalls);
        Assert.Empty(fx.Audit.Records);
    }

    // ----- подтверждение ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_single_target_asks_yes_no_and_runs_on_yes()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { Danger = ActionDanger.Confirm, Title = "Перезагрузить" };

        var result = await fx.RunAsync(action, new[] { Target("PC-1", 1) });

        var request = Assert.Single(fx.Dialogs.ConfirmRequests);
        Assert.Empty(fx.Dialogs.ReasonRequests);
        Assert.Equal("Перезагрузить", request.Title);
        Assert.Equal(new[] { "PC-1" }, request.Targets);
        Assert.Contains("«PC-1»", request.Message);
        Assert.Contains("«Перезагрузить»", request.Message);
        Assert.False(request.IsDangerous);
        Assert.True(result.AllOk);
        Assert.Single(action.Executions);
        Assert.Null(fx.Audit.Records.Single().Reason);
    }

    [Fact]
    public async Task Declined_confirmation_cancels_without_executing_or_auditing()
    {
        var fx = new Fixture();
        fx.Dialogs.ConfirmAnswer = false;
        var action = new TestAction<ComputerTarget> { Danger = ActionDanger.Confirm };

        var result = await fx.RunAsync(action, new[] { Target("PC-1", 1) });

        Assert.True(result.Cancelled);
        Assert.Empty(result.Results);
        Assert.Equal("Отменено", result.Summary);
        Assert.Empty(action.Executions);
        Assert.Empty(fx.Audit.Records);
    }

    [Fact]
    public async Task ConfirmWithReason_passes_trimmed_reason_to_action_and_audit()
    {
        var fx = new Fixture();
        fx.Dialogs.ReasonAnswer = new ReasonResult(true, "  замена диска  ");
        var action = new TestAction<ComputerTarget> { Danger = ActionDanger.ConfirmWithReason };

        var result = await fx.RunAsync(action, new[] { Target("PC-1", 1) });

        var request = Assert.Single(fx.Dialogs.ReasonRequests);
        Assert.Empty(fx.Dialogs.ConfirmRequests);
        Assert.True(request.IsDangerous);
        Assert.Contains("причин", request.Message);
        Assert.True(result.AllOk);
        Assert.Equal("замена диска", action.Executions.Single().Context.Reason);
        Assert.Equal("замена диска", fx.Audit.Records.Single().Reason);
    }

    [Theory]
    [InlineData(false, "причина")]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    [InlineData(true, null)]
    public async Task ConfirmWithReason_without_confirmation_or_reason_cancels(bool confirmed, string? reason)
    {
        var fx = new Fixture();
        fx.Dialogs.ReasonAnswer = new ReasonResult(confirmed, reason);
        var action = new TestAction<ComputerTarget> { Danger = ActionDanger.ConfirmWithReason };

        var result = await fx.RunAsync(action, new[] { Target("PC-1", 1) });

        Assert.True(result.Cancelled);
        Assert.Empty(action.Executions);
        Assert.Empty(fx.Audit.Records);
    }

    [Theory]
    [InlineData(ActionDanger.Confirm)]
    [InlineData(ActionDanger.ConfirmWithReason)]
    public async Task Bulk_dangerous_action_always_asks_for_a_reason(ActionDanger danger)
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { Danger = danger, SupportsBulk = true, Title = "Выйти из сессии" };
        var targets = Targets(3);

        var result = await fx.RunAsync(action, targets);

        Assert.Empty(fx.Dialogs.ConfirmRequests);
        var request = Assert.Single(fx.Dialogs.ReasonRequests);
        Assert.Equal(Labels(targets), request.Targets);
        Assert.True(request.IsDangerous);
        Assert.Contains("для 3 объектов", request.Message);
        Assert.Equal(3, result.Succeeded);
        Assert.All(fx.Audit.Records, r => Assert.Equal("плановые работы", r.Reason));
        Assert.All(action.Executions, e => Assert.Equal("плановые работы", e.Context.Reason));
    }

    [Fact]
    public async Task Bulk_safe_action_runs_without_any_dialog()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { Danger = ActionDanger.None, SupportsBulk = true };

        var result = await fx.RunAsync(action, Targets(5));

        Assert.Empty(fx.Dialogs.ConfirmRequests);
        Assert.Empty(fx.Dialogs.ReasonRequests);
        Assert.Equal(5, result.Succeeded);
        Assert.All(fx.Audit.Records, r => Assert.Null(r.Reason));
    }

    [Theory]
    [InlineData(21, "для 21 объекта")]
    [InlineData(12, "для 12 объектов")]
    [InlineData(2, "для 2 объектов")]
    public async Task Bulk_dialog_message_uses_correct_russian_form(int count, string expectedPhrase)
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { Danger = ActionDanger.Confirm, SupportsBulk = true };

        await fx.RunAsync(action, Targets(count));

        Assert.Contains(expectedPhrase, fx.Dialogs.ReasonRequests.Single().Message);
    }

    [Fact]
    public async Task Highest_danger_among_targets_decides_the_dialog()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            Danger = ActionDanger.None,
            DangerFunc = (t, _) => t.Label == "PC-02" ? ActionDanger.Confirm : ActionDanger.None,
        };

        await fx.RunAsync(action, new[] { Target("PC-01", 1) });
        Assert.Empty(fx.Dialogs.ConfirmRequests);

        await fx.RunAsync(action, new[] { Target("PC-02", 2) });
        Assert.Single(fx.Dialogs.ConfirmRequests);

        await fx.RunAsync(action, new[] { Target("PC-01", 1), Target("PC-02", 2) }); // пачка: подтверждение повышается до «с причиной»
        Assert.Single(fx.Dialogs.ReasonRequests);
    }

    [Fact]
    public async Task Danger_is_evaluated_with_parameters_from_the_caller_and_from_prepare()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            DangerFunc = (_, ctx) => ctx.GetFlag(ActionParameters.NoConsent) ? ActionDanger.ConfirmWithReason : ActionDanger.None,
        };
        var targets = new[] { Target("PC-1", 1) };

        await fx.RunAsync(action, targets);
        Assert.Empty(fx.Dialogs.ReasonRequests);

        await fx.RunAsync(action, targets, new Dictionary<string, string> { [ActionParameters.NoConsent] = "true" });
        Assert.Single(fx.Dialogs.ReasonRequests);

        action.PrepareFunc = (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(
            new Dictionary<string, string> { [ActionParameters.NoConsent] = "true" });
        await fx.RunAsync(action, targets);
        Assert.Equal(2, fx.Dialogs.ReasonRequests.Count);
        Assert.Equal("плановые работы", fx.Audit.Records[^1].Reason);
    }

    [Fact]
    public async Task Failing_get_danger_is_treated_as_the_most_dangerous()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { DangerFunc = (_, _) => throw new InvalidOperationException("сбой оценки") };

        await fx.RunAsync(action, new[] { Target("PC-1", 1) });

        Assert.Single(fx.Dialogs.ReasonRequests);
    }

    // ----- CanExecute ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unavailable_targets_fail_with_reason_are_not_executed_and_are_audited_as_skipped()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            CanExecuteFunc = (t, _) => t.Label != "PC-02",
            UnavailableReasonFunc = (_, _) => "Компьютер не в сети",
        };
        var progress = new ListProgress<BulkProgress>();

        var result = await fx.RunAsync(action, Targets(3), progress: progress);

        Assert.Equal(new[] { "PC-01", "PC-02", "PC-03" }, result.Results.Select(r => r.Target));
        Assert.Equal(ActionStatus.Ok, result.Results[0].Result.Status);
        Assert.Equal(ActionResult.Failed("Компьютер не в сети"), result.Results[1].Result);
        Assert.Equal(2, action.Executions.Count);
        Assert.DoesNotContain(action.Executions, e => e.Target.Label == "PC-02");
        Assert.Equal(1, result.Failed);

        var skipped = Assert.Single(fx.Audit.Records, r => r.Target == "PC-02");
        Assert.Equal(ActionStatus.Failed, skipped.Status);
        Assert.Equal("Пропущено: Компьютер не в сети", skipped.Message);
        Assert.Equal(3, fx.Audit.Records.Count);
        Assert.Equal(new[] { 1, 2, 3 }, progress.Items.Select(p => p.Done).Order());
        Assert.Equal(3, progress.Items.Count);
    }

    [Fact]
    public async Task Unavailable_target_without_reason_gets_default_text()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { CanExecuteFunc = (_, _) => false };

        var result = await fx.RunAsync(action, new[] { Target("PC-1", 1) });

        Assert.Equal("Действие недоступно", result.Results.Single().Result.Message);
    }

    [Fact]
    public async Task When_nothing_can_run_there_is_no_prepare_no_dialog_and_no_execution()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            Danger = ActionDanger.Confirm,
            CanExecuteFunc = (_, _) => false,
            UnavailableReasonFunc = (_, _) => "Нет прав",
        };

        var result = await fx.RunAsync(action, Targets(2));

        Assert.False(result.Cancelled);
        Assert.Equal(2, result.Failed);
        Assert.Equal(0, action.PrepareCalls);
        Assert.Empty(fx.Dialogs.ConfirmRequests);
        Assert.Empty(fx.Dialogs.ReasonRequests);
        Assert.Empty(action.Executions);
        Assert.Equal(2, fx.Audit.Records.Count);
        Assert.All(fx.Audit.Records, r => Assert.StartsWith("Пропущено: Нет прав", r.Message));
    }

    [Fact]
    public async Task Dialog_and_prepare_see_only_runnable_targets()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            Danger = ActionDanger.Confirm,
            SupportsBulk = true,
            CanExecuteFunc = (t, _) => t.Label != "PC-02",
        };

        await fx.RunAsync(action, Targets(3));

        Assert.Equal(new[] { "PC-01", "PC-03" }, Labels(action.PreparedWith.Single()));
        Assert.Equal(new[] { "PC-01", "PC-03" }, fx.Dialogs.ReasonRequests.Single().Targets);
    }

    [Fact]
    public async Task Skipped_targets_get_the_reason_in_audit_too()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            Danger = ActionDanger.ConfirmWithReason,
            SupportsBulk = true,
            CanExecuteFunc = (t, _) => t.Label != "PC-02",
        };

        await fx.RunAsync(action, Targets(3));

        Assert.All(fx.Audit.Records, r => Assert.Equal("плановые работы", r.Reason));
    }

    [Fact]
    public async Task Throwing_can_execute_becomes_failed_for_that_target_only()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            CanExecuteFunc = (t, _) => t.Label == "PC-01" ? throw new InvalidOperationException("сбой проверки") : true,
        };

        var result = await fx.RunAsync(action, Targets(2));

        Assert.Equal("Не удалось проверить доступность: сбой проверки", result.Results[0].Result.Message);
        Assert.Equal(ActionStatus.Ok, result.Results[1].Result.Status);
    }

    // ----- PrepareAsync и параметры --------------------------------------------------------------------------------------

    [Fact]
    public async Task Prepare_runs_once_for_the_whole_batch_before_confirmation()
    {
        var fx = new Fixture();
        var order = new List<string>();
        fx.Dialogs.ReasonAnswer = new ReasonResult(true, "причина");
        var action = new TestAction<ComputerTarget>
        {
            Danger = ActionDanger.Confirm,
            SupportsBulk = true,
            PrepareFunc = (targets, _, _) =>
            {
                order.Add($"prepare({targets.Count})");
                return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>());
            },
            ExecuteFunc = (_, _, _) =>
            {
                lock (order) order.Add("execute");
                return Task.FromResult(ActionResult.Ok());
            },
        };

        await fx.RunAsync(action, Targets(3));

        Assert.Equal(1, action.PrepareCalls);
        Assert.Equal("prepare(3)", order[0]);
        Assert.Equal(3, order.Count(x => x == "execute"));
    }

    [Fact]
    public async Task Prepared_parameters_are_merged_over_passed_ones_and_reach_every_target()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            PrepareFunc = (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(
                new Dictionary<string, string> { ["b"] = "new", ["c"] = "3" }),
        };
        var passed = new Dictionary<string, string> { ["a"] = "1", ["b"] = "old" };

        await fx.RunAsync(action, Targets(3), passed);

        Assert.Equal(3, action.Executions.Count);
        foreach (var (_, context) in action.Executions)
        {
            Assert.Equal(
                new Dictionary<string, string> { ["a"] = "1", ["b"] = "new", ["c"] = "3" }.OrderBy(p => p.Key),
                context.Parameters.OrderBy(p => p.Key));
        }

        Assert.Equal("old", passed["b"]); // переданный словарь не меняется
    }

    [Fact]
    public async Task Prepare_returning_null_cancels_without_dialog_execution_or_audit()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            Danger = ActionDanger.Confirm,
            PrepareFunc = (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(null),
        };

        var result = await fx.RunAsync(action, Targets(2));

        Assert.True(result.Cancelled);
        Assert.Empty(result.Results);
        Assert.Empty(fx.Dialogs.ConfirmRequests);
        Assert.Empty(fx.Dialogs.ReasonRequests);
        Assert.Empty(action.Executions);
        Assert.Empty(fx.Audit.Records);
    }

    [Fact]
    public async Task Prepare_cancelled_by_exception_means_cancelled()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { PrepareFunc = (_, _, _) => throw new OperationCanceledException() };

        var result = await fx.RunAsync(action, Targets(2));

        Assert.True(result.Cancelled);
        Assert.Empty(action.Executions);
    }

    [Fact]
    public async Task Prepare_failure_fails_every_target_with_the_message_and_audits_them()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            PrepareFunc = (_, _, _) => throw new OperationFailedException("Не удалось получить список групп.", OperationFailure.Unreachable),
        };

        var result = await fx.RunAsync(action, Targets(2));

        Assert.False(result.Cancelled);
        Assert.All(result.Results, r => Assert.Equal(ActionResult.Failed("Не удалось получить список групп."), r.Result));
        Assert.Empty(action.Executions);
        Assert.Equal(2, fx.Audit.Records.Count);
        Assert.All(fx.Audit.Records, r => Assert.Equal(ActionStatus.Failed, r.Status));
    }

    // ----- параллелизм ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_action_runs_in_parallel_up_to_bulk_parallelism()
    {
        var fx = new Fixture();
        fx.Settings.Settings.BulkParallelism = 3;
        var gate = new Gate();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            ExecuteFunc = async (_, _, ct) =>
            {
                await gate.WaitAsync(ct);
                return ActionResult.Ok();
            },
        };

        var run = fx.RunAsync(action, Targets(10));
        await Wait.UntilAsync(() => action.Running == 3, what: "три цели выполняются сразу");
        await Wait.HoldsAsync(() => action.Running == 3 && action.Executions.Count == 3, 150, "четвёртая не должна стартовать");

        gate.Open();
        var result = await run.WithTimeout();

        Assert.Equal(10, result.Succeeded);
        Assert.Equal(3, action.MaxRunning);
        Assert.Equal(10, fx.Audit.Records.Count);
    }

    [Fact]
    public async Task Default_bulk_parallelism_is_eight()
    {
        var fx = new Fixture();
        var gate = new Gate();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            ExecuteFunc = async (_, _, ct) =>
            {
                await gate.WaitAsync(ct);
                return ActionResult.Ok();
            },
        };

        var run = fx.RunAsync(action, Targets(20));
        await Wait.UntilAsync(() => action.Running == 8);
        await Wait.HoldsAsync(() => action.Running == 8, 100);
        gate.Open();
        await run.WithTimeout();

        Assert.Equal(8, action.MaxRunning);
    }

    [Fact]
    public async Task Action_without_bulk_support_runs_targets_one_by_one_in_order()
    {
        var fx = new Fixture();
        fx.Settings.Settings.BulkParallelism = 8;
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = false,
            ExecuteFunc = async (_, _, _) =>
            {
                await Task.Delay(5);
                return ActionResult.Ok();
            },
        };
        var targets = Targets(6);

        var result = await fx.RunAsync(action, targets);

        Assert.Equal(1, action.MaxRunning);
        Assert.Equal(Labels(targets), action.Executions.Select(e => e.Target.Label));
        Assert.Equal(6, result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Nonsense_parallelism_setting_still_runs_everything(int configured)
    {
        var fx = new Fixture();
        fx.Settings.Settings.BulkParallelism = configured;
        var action = new TestAction<ComputerTarget> { SupportsBulk = true };

        var result = await fx.RunAsync(action, Targets(4));

        Assert.Equal(4, result.Succeeded);
        Assert.Equal(1, action.MaxRunning);
    }

    [Fact]
    public async Task Results_keep_input_order_even_when_finished_out_of_order()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            ExecuteFunc = async (t, _, _) =>
            {
                await Task.Delay(t.Label == "PC-01" ? 60 : 1);
                return ActionResult.Ok(t.Label);
            },
        };
        var targets = Targets(4);

        var result = await fx.RunAsync(action, targets);

        Assert.Equal(Labels(targets), result.Results.Select(r => r.Target));
        Assert.Equal(Labels(targets), result.Results.Select(r => r.Result.Message));
    }

    [Fact]
    public async Task Every_target_gets_its_own_context_with_its_own_audit_details()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            ExecuteFunc = async (t, ctx, _) =>
            {
                await Task.Yield();
                ctx.AuditDetails = "детали " + t.Label;
                return ActionResult.Ok();
            },
        };

        await fx.RunAsync(action, Targets(5));

        Assert.Equal(5, action.Executions.Select(e => e.Context).Distinct().Count());
        foreach (var record in fx.Audit.Records)
            Assert.Equal("детали " + record.Target, record.Details);
    }

    // ----- ошибки ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exceptions_become_results_and_do_not_stop_other_targets()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            ExecuteFunc = (t, _, _) => t.Label switch
            {
                "PC-01" => throw new OperationFailedException("Нет прав на компьютере (5).", OperationFailure.AccessDenied),
                "PC-02" => throw new InvalidOperationException("что-то сломалось"),
                "PC-03" => throw new OperationCanceledException(),
                "PC-04" => Task.FromResult(ActionResult.Cancelled("пользователь отказался")),
                "PC-05" => Task.FromResult<ActionResult>(null!),
                _ => Task.FromResult(ActionResult.Ok("готово")),
            },
        };

        var result = await fx.RunAsync(action, Targets(6));

        Assert.Equal(ActionResult.Failed("Нет прав на компьютере (5)."), result.Results[0].Result);
        Assert.Equal(ActionResult.Failed("Непредвиденная ошибка: что-то сломалось"), result.Results[1].Result);
        Assert.Equal(ActionStatus.Cancelled, result.Results[2].Result.Status);
        Assert.Equal(ActionResult.Cancelled("пользователь отказался"), result.Results[3].Result);
        Assert.Equal(ActionStatus.Failed, result.Results[4].Result.Status);
        Assert.Equal(ActionResult.Ok("готово"), result.Results[5].Result);
        Assert.False(result.Cancelled); // токен не отменяли
        Assert.Equal("1 из 6 успешно", result.Summary);

        var statuses = fx.Audit.Records.OrderBy(r => r.Target).Select(r => r.Status).ToArray();
        Assert.Equal(
            new[] { ActionStatus.Failed, ActionStatus.Failed, ActionStatus.Cancelled, ActionStatus.Cancelled, ActionStatus.Failed, ActionStatus.Ok },
            statuses);
        Assert.Equal("Нет прав на компьютере (5).", fx.Audit.Records.Single(r => r.Target == "PC-01").Message);
    }

    [Fact]
    public async Task Audit_failure_does_not_break_the_run()
    {
        var fx = new Fixture();
        fx.Audit.ThrowOnWrite = new IOException("диск журнала недоступен");
        var action = new TestAction<ComputerTarget> { SupportsBulk = true };

        var result = await fx.RunAsync(action, Targets(3));

        Assert.Equal(3, result.Succeeded);
    }

    // ----- журнал и секреты --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Passwords_from_parameters_never_reach_the_audit_log_or_dialogs()
    {
        var fx = new Fixture();
        const string typed = "T0pSecret!pwd";
        const string prepared = "N3wPassw0rd#123";
        var seen = new List<(string? Typed, string? Prepared)>();
        var action = new TestAction<ComputerTarget>
        {
            Id = "user.reset-password",
            Title = "Сбросить пароль",
            Danger = ActionDanger.ConfirmWithReason,
            SupportsBulk = true,
            PrepareFunc = (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(
                new Dictionary<string, string> { ["newPassword"] = prepared }),
            ExecuteFunc = (t, ctx, _) =>
            {
                // Действие видит пароль, но в результат и в детали его не кладёт.
                lock (seen) seen.Add((ctx.GetParameter("password"), ctx.GetParameter("newPassword")));
                ctx.AuditDetails = "пароль сброшен, сменить при входе";
                return Task.FromResult(ActionResult.Ok("Пароль сброшен"));
            },
        };
        var parameters = new Dictionary<string, string> { ["password"] = typed, ["mustChange"] = "true" };

        await fx.RunAsync(action, Targets(2), parameters);

        var dump = JsonSerializer.Serialize(fx.Audit.Records) +
                   JsonSerializer.Serialize(fx.Dialogs.ReasonRequests) +
                   JsonSerializer.Serialize(fx.Dialogs.ConfirmRequests);
        Assert.DoesNotContain(typed, dump);
        Assert.DoesNotContain(prepared, dump);
        Assert.Equal(2, fx.Audit.Records.Count);
        Assert.All(seen, s => Assert.Equal((typed, prepared), (s.Typed, s.Prepared))); // действие получило оба пароля
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Passwords_do_not_end_up_in_the_audit_file_on_disk()
    {
        using var temp = new TempDir();
        var settings = new FakeSettingsStore();
        var log = new JsonAuditLog(temp.Paths, settings);
        var dialogs = new FakeDialogService();
        var connection = FakeCurrentConnection.LoggedIn();
        var provider = new SimpleServiceProvider().Add<IDialogService>(dialogs);
        var runner = new ActionRunner(provider, log, settings, dialogs, connection);
        const string secret = "S3cret-Value-For-Test";
        var action = new TestAction<ComputerTarget>
        {
            Danger = ActionDanger.Confirm,
            ExecuteFunc = (_, _, _) => Task.FromResult(ActionResult.Failed("Не удалось сбросить пароль (политика сложности)")),
        };

        await runner.RunAsync(action, new[] { Target("PC-1", 1) }, new Dictionary<string, string> { ["password"] = secret }, null, default);

        var files = Directory.GetFiles(temp.Paths.AuditDir, "*.jsonl");
        var text = string.Concat(files.Select(File.ReadAllText));
        Assert.Single(files);
        Assert.DoesNotContain(secret, text);
        Assert.Contains("PC-1", text);
        Assert.Contains("политика сложности", text);
        var read = await log.ReadRecentAsync(10);
        Assert.Equal(ActionStatus.Failed, read.Single().Status);
    }

    // ----- прогресс -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Progress_is_reported_after_each_target()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { SupportsBulk = true };
        var progress = new ListProgress<BulkProgress>();
        var targets = Targets(5);

        await fx.RunAsync(action, targets, progress: progress);

        var items = progress.Items;
        Assert.Equal(5, items.Count);
        Assert.All(items, p => Assert.Equal(5, p.Total));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, items.Select(p => p.Done).Order());
        Assert.Equal(Labels(targets).Order(), items.Select(p => p.Current!).Order());
    }

    // ----- отмена --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Already_cancelled_token_returns_cancelled_without_anything_happening()
    {
        var fx = new Fixture();
        var action = new TestAction<ComputerTarget> { Danger = ActionDanger.Confirm };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await fx.RunAsync(action, Targets(2), ct: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Empty(result.Results);
        Assert.Equal(0, action.PrepareCalls);
        Assert.Empty(fx.Dialogs.ConfirmRequests);
        Assert.Empty(fx.Audit.Records);
    }

    [Fact]
    public async Task Cancellation_during_sequential_run_leaves_remaining_targets_unstarted()
    {
        var fx = new Fixture();
        using var cts = new CancellationTokenSource();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = false,
            ExecuteFunc = async (t, _, _) =>
            {
                if (t.Label == "PC-02") await cts.CancelAsync();
                return ActionResult.Ok();
            },
        };

        var result = await fx.RunAsync(action, Targets(5), ct: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(2, action.Executions.Count);
        Assert.Equal(5, result.Results.Count);
        Assert.Equal(ActionStatus.Ok, result.Results[0].Result.Status);
        Assert.Equal(ActionStatus.Ok, result.Results[1].Result.Status);
        Assert.All(result.Results.Skip(2), r => Assert.Equal(ActionStatus.Cancelled, r.Result.Status));
        Assert.Equal(2, fx.Audit.Records.Count); // не начатые цели в журнал не пишутся
    }

    [Fact]
    public async Task Cancellation_during_parallel_run_stops_running_and_unstarted_targets()
    {
        var fx = new Fixture();
        fx.Settings.Settings.BulkParallelism = 3;
        var gate = new Gate();
        using var cts = new CancellationTokenSource();
        var action = new TestAction<ComputerTarget>
        {
            SupportsBulk = true,
            ExecuteFunc = async (_, _, ct) =>
            {
                await gate.WaitAsync(ct); // держит цель до отмены
                return ActionResult.Ok();
            },
        };

        var run = fx.RunAsync(action, Targets(10), ct: cts.Token);
        await Wait.UntilAsync(() => action.Running == 3);
        await cts.CancelAsync();
        var result = await run.WithTimeout();

        Assert.True(result.Cancelled);
        Assert.Equal(10, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(ActionStatus.Cancelled, r.Result.Status));
        Assert.Equal(3, action.Executions.Count);
        Assert.Equal(0, action.Running);
        Assert.Equal(3, fx.Audit.Records.Count); // только те, что успели начаться
    }

    [Fact]
    public async Task Everything_finished_before_cancellation_is_not_reported_as_cancelled()
    {
        var fx = new Fixture();
        using var cts = new CancellationTokenSource();
        var action = new TestAction<ComputerTarget>();

        var result = await fx.RunAsync(action, Targets(2), ct: cts.Token);
        await cts.CancelAsync();

        Assert.False(result.Cancelled);
        Assert.True(result.AllOk);
    }
}
