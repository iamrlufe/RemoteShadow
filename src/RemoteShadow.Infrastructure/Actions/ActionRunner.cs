using System.Collections.Concurrent;
using System.Globalization;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Запускает действия. Порядок:
/// 1) цели, для которых <c>CanExecute</c> = false, получают <see cref="ActionStatus.Failed"/> с причиной
///    (<c>GetUnavailableReason</c> или «Действие недоступно»). Они не выполняются, но попадают в журнал как Failed с пометкой
///    «Пропущено: …», чтобы в журнале было видно, что попытка была. Если выполнять нечего, возвращается итог без подготовки и диалогов;
/// 2) <c>PrepareAsync</c> вызывается один раз для всех оставшихся целей; null — пользователь отменил;
/// 3) подтверждение по максимальной опасности среди целей (<c>GetDanger</c>); для пачки из нескольких целей — не ниже
///    <see cref="ActionDanger.ConfirmWithReason"/>; <see cref="ActionDanger.None"/> — без диалога;
/// 4) выполнение: если действие поддерживает пачки — параллельно (не больше <see cref="AppSettings.BulkParallelism"/>), иначе по очереди;
/// 5) после каждой цели — запись в журнал и <see cref="IProgress{T}"/>.
/// Параметры (<see cref="ActionContext.Parameters"/>) в журнал не попадают никогда: там могут быть пароли.
/// Отказ в диалоге или отмена подготовки дают пустой итог с <see cref="BulkResult.Cancelled"/> и ничего не пишут в журнал.
/// Если токен отменён во время выполнения, не начатые цели получают <see cref="ActionStatus.Cancelled"/> (в журнал не пишутся).
/// </summary>
public sealed class ActionRunner : IActionRunner
{
    private static readonly IReadOnlyDictionary<string, string> NoParameters = new Dictionary<string, string>();
    private static readonly StringComparer RussianComparer = StringComparer.Create(new CultureInfo("ru-RU"), ignoreCase: true);

    private readonly IServiceProvider _services;
    private readonly IAuditLog _audit;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly ICurrentConnection _connection;
    private readonly ConcurrentDictionary<Type, object> _sortedActions = new();

    public ActionRunner(
        IServiceProvider services,
        IAuditLog audit,
        ISettingsStore settings,
        IDialogService dialogs,
        ICurrentConnection connection)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public IReadOnlyList<IAction<TTarget>> GetActions<TTarget>() where TTarget : IActionTarget =>
        (IReadOnlyList<IAction<TTarget>>)_sortedActions.GetOrAdd(typeof(TTarget), _ =>
            ((_services.GetService(typeof(IEnumerable<IAction<TTarget>>)) as IEnumerable<IAction<TTarget>>)
                ?? Array.Empty<IAction<TTarget>>())
            .OrderBy(a => a.Group, RussianComparer)
            .ThenBy(a => a.Order)
            .ThenBy(a => a.Title, RussianComparer)
            .ToList());

    public ActionContext CreateContext(IReadOnlyDictionary<string, string>? parameters = null) =>
        new(_services, parameters);

    public async Task<BulkResult> RunAsync<TTarget>(
        IAction<TTarget> action,
        IReadOnlyList<TTarget> targets,
        IReadOnlyDictionary<string, string>? parameters,
        IProgress<BulkProgress>? progress,
        CancellationToken ct) where TTarget : IActionTarget
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0) return new BulkResult(Array.Empty<TargetResult>(), false);
        if (ct.IsCancellationRequested) return Cancelled();

        var done = 0;
        void Report(TTarget target) =>
            progress?.Report(new BulkProgress(Interlocked.Increment(ref done), targets.Count, target.Label));

        var checkContext = new ActionContext(_services, parameters);
        var slots = new TargetResult?[targets.Count];

        // 1. Что можно выполнить.
        var runnable = new List<int>(targets.Count);
        var skipped = new List<int>();
        for (var i = 0; i < targets.Count; i++)
        {
            var unavailable = GetUnavailableReason(action, targets[i], checkContext);
            if (unavailable is null)
            {
                runnable.Add(i);
            }
            else
            {
                slots[i] = new TargetResult(targets[i].Label, ActionResult.Failed(unavailable));
                skipped.Add(i);
            }
        }

        async Task AuditSkippedAsync(string? reason)
        {
            foreach (var i in skipped)
            {
                var skippedResult = ActionResult.Failed("Пропущено: " + slots[i]!.Result.Message);
                await WriteAuditAsync(action, targets[i], skippedResult, reason, null).ConfigureAwait(false);
                Report(targets[i]);
            }
        }

        if (runnable.Count == 0)
        {
            await AuditSkippedAsync(null).ConfigureAwait(false);
            return Finish(slots, false);
        }

        var runTargets = runnable.Select(i => targets[i]).ToList();

        // 2. Подготовка (ввод текста, пароля и т. п.), один раз на всю пачку. Диалоги зовутся из потока вызывающего.
        IReadOnlyDictionary<string, string>? prepared;
        try
        {
            prepared = await action.PrepareAsync(runTargets, checkContext, ct);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception ex)
        {
            var failure = ToResult(ex);
            await AuditSkippedAsync(null).ConfigureAwait(false);
            foreach (var i in runnable)
            {
                slots[i] = new TargetResult(targets[i].Label, failure);
                await WriteAuditAsync(action, targets[i], failure, null, null).ConfigureAwait(false);
                Report(targets[i]);
            }

            return Finish(slots, false);
        }

        if (prepared is null) return Cancelled();

        var merged = Merge(parameters, prepared);

        // 3. Подтверждение.
        var danger = MaxDanger(action, runTargets, new ActionContext(_services, merged));
        if (runTargets.Count > 1 && danger == ActionDanger.Confirm)
            danger = ActionDanger.ConfirmWithReason;

        string? reason = null;
        if (danger == ActionDanger.Confirm)
        {
            if (!await _dialogs.ConfirmAsync(BuildRequest(action, runTargets, danger))) return Cancelled();
        }
        else if (danger == ActionDanger.ConfirmWithReason)
        {
            var answer = await _dialogs.ConfirmWithReasonAsync(BuildRequest(action, runTargets, danger));
            reason = answer.Reason?.Trim();
            if (!answer.Confirmed || string.IsNullOrEmpty(reason)) return Cancelled();
        }

        if (ct.IsCancellationRequested) return Cancelled();

        // 4–5. Выполнение, журнал, прогресс.
        await AuditSkippedAsync(reason).ConfigureAwait(false);

        var parallelism = action.SupportsBulk && runnable.Count > 1
            ? Math.Min(Math.Clamp(_settings.Load().BulkParallelism, 1, 64), runnable.Count)
            : 1;

        var next = -1;
        var workers = Enumerable.Range(0, parallelism).Select(_ => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var k = Interlocked.Increment(ref next);
                if (k >= runnable.Count) return;

                var index = runnable[k];
                slots[index] = await ExecuteOneAsync(action, targets[index], merged, reason, ct).ConfigureAwait(false);
                Report(targets[index]);
            }
        }, CancellationToken.None));
        await Task.WhenAll(workers);

        var notStarted = false;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not null) continue;
            slots[i] = new TargetResult(targets[i].Label, ActionResult.Cancelled("Отменено"));
            notStarted = true;
        }

        var cancelled = ct.IsCancellationRequested
                        && (notStarted || slots.Any(s => s!.Result.Status == ActionStatus.Cancelled));
        return Finish(slots, cancelled);
    }

    private async Task<TargetResult> ExecuteOneAsync<TTarget>(
        IAction<TTarget> action,
        TTarget target,
        IReadOnlyDictionary<string, string> parameters,
        string? reason,
        CancellationToken ct) where TTarget : IActionTarget
    {
        var context = new ActionContext(_services, parameters, reason);

        ActionResult result;
        try
        {
            result = await action.ExecuteAsync(target, context, ct).ConfigureAwait(false)
                     ?? ActionResult.Failed("Действие не вернуло результат.");
        }
        catch (Exception ex)
        {
            result = ToResult(ex);
        }

        await WriteAuditAsync(action, target, result, reason, context.AuditDetails).ConfigureAwait(false);
        return new TargetResult(target.Label, result);
    }

    private static ActionResult ToResult(Exception ex) => ex switch
    {
        OperationCanceledException => ActionResult.Cancelled("Отменено"),
        OperationFailedException => ActionResult.Failed(ex.Message),
        _ => ActionResult.Failed("Непредвиденная ошибка: " + ex.Message),
    };

    private async Task WriteAuditAsync<TTarget>(
        IAction<TTarget> action, TTarget target, ActionResult result, string? reason, string? details)
        where TTarget : IActionTarget
    {
        // Parameters сюда намеренно не попадают: в них бывают пароли.
        var record = new AuditRecord
        {
            Account = AccountName(),
            ActionId = action.Id,
            ActionTitle = action.Title,
            TargetKind = target.Kind,
            Target = target.Label,
            Reason = reason,
            Status = result.Status,
            Message = result.Message,
            Details = details,
        };

        try
        {
            await _audit.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Контракт IAuditLog — не бросать, но чужая реализация может ошибиться; действие от этого не отменяется.
        }
    }

    private static string? GetUnavailableReason<TTarget>(IAction<TTarget> action, TTarget target, ActionContext context)
        where TTarget : IActionTarget
    {
        try
        {
            return action.CanExecute(target, context)
                ? null
                : action.GetUnavailableReason(target, context) ?? "Действие недоступно";
        }
        catch (Exception ex)
        {
            return "Не удалось проверить доступность: " + ex.Message;
        }
    }

    private static ActionDanger MaxDanger<TTarget>(IAction<TTarget> action, IReadOnlyList<TTarget> targets, ActionContext context)
        where TTarget : IActionTarget
    {
        var max = ActionDanger.None;
        foreach (var target in targets)
        {
            ActionDanger danger;
            try
            {
                danger = action.GetDanger(target, context);
            }
            catch
            {
                danger = ActionDanger.ConfirmWithReason; // Не смогли оценить — считаем самым опасным.
            }

            if (danger > max) max = danger;
        }

        return max;
    }

    private static ConfirmRequest BuildRequest<TTarget>(IAction<TTarget> action, IReadOnlyList<TTarget> targets, ActionDanger danger)
        where TTarget : IActionTarget
    {
        var labels = targets.Select(t => t.Label).ToList();

        var subject = targets.Count == 1
            ? $"«{labels[0]}»"
            : $"{targets.Count} {RussianPlural.Choose(targets.Count, "объекта", "объектов", "объектов")}";

        var message = $"Выполнить «{action.Title}» для {subject}?";
        if (danger == ActionDanger.ConfirmWithReason)
            message += " Укажите причину — она будет записана в журнал.";

        // «Опасное» подтверждение (красная кнопка) — когда нужна причина или затронуто сразу несколько объектов.
        var dangerous = danger == ActionDanger.ConfirmWithReason || targets.Count > 1;
        return new ConfirmRequest(action.Title, message, labels, dangerous);
    }

    // Значения из PrepareAsync перекрывают переданные параметры.
    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? parameters, IReadOnlyDictionary<string, string> prepared)
    {
        var merged = new Dictionary<string, string>(parameters ?? NoParameters);
        foreach (var (key, value) in prepared) merged[key] = value;
        return merged;
    }

    private string AccountName() =>
        _connection.Current?.AccountName is { Length: > 0 } account
            ? account
            : $"{Environment.UserDomainName}\\{Environment.UserName}";

    private static BulkResult Cancelled() => new(Array.Empty<TargetResult>(), true);

    private static BulkResult Finish(TargetResult?[] slots, bool cancelled) =>
        new(slots.Select(s => s!).ToList(), cancelled);
}
