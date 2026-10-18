using System.Globalization;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>
/// Перезагрузка или выключение с предупреждением пользователям. Задержка и текст спрашиваются один раз на всю пачку
/// в <see cref="PrepareAsync"/> и передаются параметрами <see cref="DelayParameter"/> и <see cref="MessageParameter"/>.
/// </summary>
public abstract class PowerAction : IAction<ComputerTarget>
{
    /// <summary>Имя параметра с задержкой в секундах.</summary>
    public const string DelayParameter = "delaySeconds";

    /// <summary>Имя параметра с предупреждением для пользователей.</summary>
    public const string MessageParameter = "message";

    /// <summary>Наименьшая задержка: 0 — сразу.</summary>
    public const int MinDelaySeconds = 0;

    /// <summary>Наибольшая задержка: час.</summary>
    public const int MaxDelaySeconds = 3600;

    /// <summary>Задержка, предлагаемая в диалоге.</summary>
    public const int DefaultDelaySeconds = 60;

    /// <summary>Предел длины предупреждения: длиннее смысла нет, а API завершения работы принимает короткие комментарии.</summary>
    public const int MaxMessageLength = 512;

    private readonly bool _reboot;

    protected PowerAction(string id, string title, string icon, int order, string description, bool reboot)
    {
        Id = id;
        Title = title;
        Icon = icon;
        Order = order;
        Description = description;
        _reboot = reboot;
    }

    public string Id { get; }
    public string Title { get; }
    public string Group => ComputerPowerModule.GroupName;
    public string? Icon { get; }
    public int Order { get; }
    public string? Description { get; }
    public ActionDanger Danger => ActionDanger.ConfirmWithReason;
    public bool SupportsBulk => true;

    public bool CanExecute(ComputerTarget target, ActionContext context) => true;

    /// <summary>Допустимая задержка: целое число от 0 до 3600 без знака и пробелов.</summary>
    public static bool TryParseDelay(string? text, out int seconds) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
        && seconds is >= MinDelaySeconds and <= MaxDelaySeconds;

    /// <summary>Предупреждение по умолчанию: «Компьютер будет перезагружен администратором через 60 секунд. Сохраните работу.».</summary>
    public static string DefaultMessage(bool reboot, int delaySeconds)
    {
        var action = reboot ? "перезагружен" : "выключен";
        var when = delaySeconds <= 0
            ? "немедленно"
            : $"через {delaySeconds.ToString(CultureInfo.InvariantCulture)} {SecondsWord(delaySeconds)}";
        return $"Компьютер будет {action} администратором {when}. Сохраните работу.";
    }

    /// <summary>«секунду», «секунды», «секунд» для числа после предлога «через».</summary>
    public static string SecondsWord(int n)
    {
        var lastTwo = n % 100;
        if (lastTwo is >= 11 and <= 14) return "секунд";
        return (n % 10) switch
        {
            1 => "секунду",
            2 or 3 or 4 => "секунды",
            _ => "секунд",
        };
    }

    public async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IReadOnlyList<ComputerTarget> targets, ActionContext context, CancellationToken ct)
    {
        var dialogTitle = _reboot ? "Перезагрузка" : "Выключение";
        var scope = targets.Count switch
        {
            0 => string.Empty,
            1 => targets[0].Label + "\n",
            _ => $"Компьютеров: {targets.Count}\n",
        };

        var delay = await AskDelayAsync(dialogTitle, scope, context, ct);
        if (delay is null) return null;

        var initial = DefaultMessage(_reboot, delay.Value);
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var text = await context.Dialogs.PromptTextAsync(dialogTitle, "Предупреждение для пользователей:", initial);
            if (text is null) return null;

            text = text.Trim();
            if (text.Length <= MaxMessageLength)
            {
                return new Dictionary<string, string>
                {
                    [DelayParameter] = delay.Value.ToString(CultureInfo.InvariantCulture),
                    [MessageParameter] = text,
                };
            }

            await context.Dialogs.ShowErrorAsync(dialogTitle,
                $"Слишком длинное предупреждение: не больше {MaxMessageLength} символов.");
            initial = text;
        }
    }

    private static async Task<int?> AskDelayAsync(string dialogTitle, string scope, ActionContext context, CancellationToken ct)
    {
        var initial = DefaultDelaySeconds.ToString(CultureInfo.InvariantCulture);
        var prompt = $"{scope}Через сколько секунд выполнить ({MinDelaySeconds}–{MaxDelaySeconds}):";

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var input = await context.Dialogs.PromptTextAsync(dialogTitle, prompt, initial);
            if (input is null) return null;

            input = input.Trim();
            if (TryParseDelay(input, out var seconds)) return seconds;

            await context.Dialogs.ShowErrorAsync(dialogTitle,
                $"Введите целое число секунд от {MinDelaySeconds} до {MaxDelaySeconds}.");
            initial = input;
        }
    }

    public async Task<ActionResult> ExecuteAsync(ComputerTarget target, ActionContext context, CancellationToken ct)
    {
        var host = HostGuard.Require(target.Computer.HostName);
        if (!TryParseDelay(context.GetParameter(DelayParameter), out var seconds))
            throw new OperationFailedException(
                $"Не задана задержка: нужно целое число секунд от {MinDelaySeconds} до {MaxDelaySeconds}.", OperationFailure.InvalidInput);

        var message = context.GetParameter(MessageParameter);
        if (string.IsNullOrWhiteSpace(message)) message = null;

        await context.Sessions.ShutdownAsync(host, _reboot, TimeSpan.FromSeconds(seconds), message, ct);

        var what = _reboot ? "перезагрузка" : "выключение";
        var details = $"{what} через {seconds.ToString(CultureInfo.InvariantCulture)} с";
        context.AuditDetails = details;
        return ActionResult.Ok($"Команда отправлена: {details}");
    }
}

/// <summary>Перезагрузка компьютера.</summary>
public sealed class RebootAction()
    : PowerAction("power.reboot", "Перезагрузить", "refresh", 10,
        "Перезагрузить компьютер с предупреждением пользователям", reboot: true);

/// <summary>Выключение компьютера.</summary>
public sealed class ShutdownAction()
    : PowerAction("power.shutdown", "Выключить", "power", 20,
        "Выключить компьютер с предупреждением пользователям", reboot: false);
