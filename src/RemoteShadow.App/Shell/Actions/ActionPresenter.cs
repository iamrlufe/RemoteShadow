using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell.Actions;

/// <summary>
/// Превращает действия модулей в кнопки для выбранных объектов и запускает их через <see cref="IActionRunner"/>.
/// Один экземпляр на область страницы (карточка компьютера, карточка сессии, список пользователей): пока идёт
/// действие, <see cref="IsBusy"/> истинно и все кнопки этого экземпляра недоступны.
/// <code>
/// Buttons = presenter.BuildButtons(selectedComputers.Select(c =&gt; new ComputerTarget(c)).ToList(), parameters);
/// </code>
/// Подтверждение, пачки и запись в журнал делает <see cref="IActionRunner"/>; здесь только отбор кнопок, индикатор
/// выполнения и итоговое уведомление.
/// </summary>
public sealed partial class ActionPresenter<TTarget> : ObservableObject where TTarget : IActionTarget
{
    private readonly IActionRunner _runner;
    private readonly INotificationService _notifications;
    private readonly List<RelayCommand> _commands = new();
    private CancellationTokenSource? _cts;

    public ActionPresenter(IActionRunner runner, INotificationService notifications)
    {
        _runner = runner;
        _notifications = notifications;
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
    }

    /// <summary>true, пока выполняется действие (в том числе пока открыт диалог подтверждения).</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Что выполняется: «Перезагрузить…». null, когда ничего.</summary>
    [ObservableProperty]
    private string? _busyText;

    /// <summary>Ход пачки: «3 из 8 · PC-ACC-014». null, когда ничего или одна цель.</summary>
    [ObservableProperty]
    private string? _progressText;

    /// <summary>Отменяет выполняемое действие (не начатые цели пачки). Доступна, пока <see cref="IsBusy"/>.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Кнопки для выбранных целей, в порядке групп и <c>Order</c> действий. Одна цель: показываются все действия,
    /// недоступные — выключенными, с причиной в подсказке. Несколько целей: только действия с
    /// <see cref="IAction{TTarget}.SupportsBulk"/>, у которых <c>CanExecute</c> истинно хотя бы для одной цели; в подписи —
    /// число таких целей («Перезагрузить (3)»), и выполняются только они.
    /// </summary>
    /// <param name="parameters">
    /// Параметры из интерфейса (например, <see cref="ActionParameters.NoConsent"/>). Словарь читается и в момент
    /// построения, и в момент нажатия; если от параметров зависит доступность кнопок, после их смены вызовите метод заново.
    /// </param>
    public IReadOnlyList<ActionButtonModel> BuildButtons(
        IReadOnlyList<TTarget> targets,
        IReadOnlyDictionary<string, string>? parameters)
    {
        _commands.Clear();
        var buttons = new List<ActionButtonModel>();
        if (targets.Count == 0) return buttons;

        var context = _runner.CreateContext(parameters);
        foreach (var action in _runner.GetActions<TTarget>())
        {
            if (targets.Count == 1)
            {
                var target = targets[0];
                var canExecute = Try(() => action.CanExecute(target, context));
                var reason = canExecute ? null : Try(() => action.GetUnavailableReason(target, context));
                buttons.Add(CreateButton(action, action.Title, canExecute, reason, targets, parameters));
                continue;
            }

            if (!action.SupportsBulk) continue;

            var applicable = targets.Where(t => Try(() => action.CanExecute(t, context))).ToList();
            if (applicable.Count == 0) continue;

            buttons.Add(CreateButton(action, $"{action.Title} ({applicable.Count})", true, null, applicable, parameters));
        }

        return buttons;
    }

    /// <summary>
    /// Запускает действие для целей и показывает итог уведомлением: успех, ошибка с текстом; при отмене пользователем
    /// ничего. Не бросает исключения. Кнопки из <see cref="BuildButtons"/> вызывают именно его.
    /// </summary>
    public async Task RunAsync(
        IAction<TTarget> action,
        IReadOnlyList<TTarget> targets,
        IReadOnlyDictionary<string, string>? parameters)
    {
        if (IsBusy || targets.Count == 0) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        BusyText = $"{action.Title}…";
        ProgressText = null;
        IsBusy = true;

        try
        {
            var progress = targets.Count > 1
                ? new Progress<BulkProgress>(p =>
                {
                    if (ReferenceEquals(_cts, cts)) ProgressText = FormatProgress(p);
                })
                : null;

            var result = await _runner.RunAsync(action, targets, parameters, progress, cts.Token);
            Report(result);
        }
        catch (OperationCanceledException)
        {
            // Пользователь отменил действие: сообщать нечего.
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Действие «{action.Id}»", ex);
            _notifications.Error(ex is OperationFailedException
                ? ex.Message
                : $"Не удалось выполнить «{action.Title}»: {ex.Message}");
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsBusy = false;
            BusyText = null;
            ProgressText = null;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        foreach (var command in _commands) command.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private ActionButtonModel CreateButton(
        IAction<TTarget> action,
        string title,
        bool canExecute,
        string? reason,
        IReadOnlyList<TTarget> targets,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var command = new RelayCommand(
            () => _ = RunAsync(action, targets, parameters),
            () => canExecute && !IsBusy);
        _commands.Add(command);

        return new ActionButtonModel(
            action.Id,
            title,
            action.Group,
            action.Icon,
            action.Description,
            canExecute,
            reason,
            command,
            action.Danger);
    }

    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Действие уже завершилось.
        }
    }

    private void Report(BulkResult result)
    {
        if (result.Total == 0) return;

        if (result.AllOk)
        {
            _notifications.Success(result.Summary);
            return;
        }

        // Отказ в диалоге и отмена без ошибок — не повод для уведомления.
        if (result.Failed == 0) return;

        _notifications.Error(FormatFailure(result));
    }

    /// <summary>Текст ошибки: для одной цели её сообщение, для пачки — итог и первые причины.</summary>
    internal static string FormatFailure(BulkResult result)
    {
        var failed = result.Results.Where(r => r.Result.Status == ActionStatus.Failed).ToList();
        if (result.Total == 1) return failed[0].Result.Message ?? "Не удалось выполнить действие.";

        const int shown = 3;
        var details = failed
            .Take(shown)
            .Select(r => string.IsNullOrWhiteSpace(r.Result.Message) ? r.Target : $"{r.Target}: {r.Result.Message}");
        var text = $"{result.Summary}. {string.Join("; ", details)}";
        return failed.Count > shown ? $"{text} (и ещё {failed.Count - shown})" : text;
    }

    internal static string FormatProgress(BulkProgress progress) =>
        string.IsNullOrWhiteSpace(progress.Current)
            ? $"{progress.Done} из {progress.Total}"
            : $"{progress.Done} из {progress.Total} · {progress.Current}";

    // Проверки доступности вызываются при отрисовке кнопок: сбой одного действия не должен ломать всю панель.
    private static bool Try(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? Try(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
