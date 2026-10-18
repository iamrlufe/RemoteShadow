using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Core.Actions;

public enum ActionStatus
{
    Ok,
    Failed,
    Cancelled,
}

public enum ActionDanger
{
    /// <summary>Выполняется сразу.</summary>
    None,

    /// <summary>Диалог «Да/Нет» с перечнем целей.</summary>
    Confirm,

    /// <summary>Диалог с обязательной причиной, которая попадает в журнал.</summary>
    ConfirmWithReason,
}

public sealed record ActionResult(ActionStatus Status, string? Message = null)
{
    public bool IsOk => Status == ActionStatus.Ok;

    public static ActionResult Ok(string? message = null) => new(ActionStatus.Ok, message);
    public static ActionResult Failed(string message) => new(ActionStatus.Failed, message);
    public static ActionResult Cancelled(string? message = null) => new(ActionStatus.Cancelled, message);
}

/// <summary>Объект, над которым выполняется действие.</summary>
public interface IActionTarget
{
    /// <summary>computer, session, user — в журнал.</summary>
    string Kind { get; }

    /// <summary>Как показывать цель в диалогах и журнале: «PC-ACC-014», «PC-ACC-014 · сессия 1 (Петрова А.)».</summary>
    string Label { get; }
}

public sealed record ComputerTarget(ComputerInfo Computer) : IActionTarget
{
    public string Kind => "computer";
    public string Label => Computer.Name;
}

public sealed record SessionTarget(ComputerInfo Computer, SessionInfo Session) : IActionTarget
{
    public string Kind => "session";

    public string Label => Session.HasUser
        ? $"{Computer.Name} · сессия {Session.Id} ({Session.UserName})"
        : $"{Computer.Name} · сессия {Session.Id}";
}

public sealed record UserTarget(UserInfo User) : IActionTarget
{
    public string Kind => "user";
    public string Label => string.IsNullOrWhiteSpace(User.DisplayName) ? User.SamAccountName : $"{User.DisplayName} ({User.SamAccountName})";
}

/// <summary>Имена параметров, которые оболочка передаёт действиям через <see cref="ActionContext.Parameters"/>.</summary>
public static class ActionParameters
{
    /// <summary>"true" — shadow без запроса пользователю (галочка в интерфейсе).</summary>
    public const string NoConsent = "noConsent";
}

/// <summary>
/// Одна кнопка или пункт меню для объекта. Чтобы добавить функцию, достаточно класса, реализующего это
/// интерфейс, и строки <c>services.AddAction&lt;TTarget, TAction&gt;()</c> в модуле. Оболочка сама покажет кнопку,
/// спросит подтверждение, обработает пачку и запишет в журнал. Действие создаётся через DI (singleton): без состояния.
/// </summary>
public interface IAction<TTarget> where TTarget : IActionTarget
{
    string Id { get; }

    /// <summary>«Shadow: управление».</summary>
    string Title { get; }

    /// <summary>Группировка кнопок: «Подключение», «Сессия», «AD».</summary>
    string Group { get; }

    /// <summary>Имя значка Segoe Fluent Icons / Tabler или null.</summary>
    string? Icon { get; }

    ActionDanger Danger { get; }

    /// <summary>Можно ли выполнить для нескольких целей сразу.</summary>
    bool SupportsBulk { get; }

    /// <summary>Порядок внутри группы; меньше — выше.</summary>
    int Order => 0;

    /// <summary>Подсказка для кнопки.</summary>
    string? Description => null;

    /// <summary>Фактическая опасность для конкретной цели и параметров (например, shadow без запроса требует причину).</summary>
    ActionDanger GetDanger(TTarget target, ActionContext context) => Danger;

    bool CanExecute(TTarget target, ActionContext context);

    /// <summary>Почему кнопка недоступна — для подсказки. null, если доступна или причина не нужна.</summary>
    string? GetUnavailableReason(TTarget target, ActionContext context) => null;

    /// <summary>
    /// Один раз на всю пачку, до подтверждения: спросить ввод (текст сообщения, новый пароль) и вернуть параметры,
    /// которые попадут в <see cref="ActionContext.Parameters"/> каждой цели. null — пользователь отменил.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IReadOnlyList<TTarget> targets, ActionContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>());

    Task<ActionResult> ExecuteAsync(TTarget target, ActionContext context, CancellationToken ct);
}

/// <summary>Всё, что нужно действию. Создаётся оболочкой на каждую цель.</summary>
public sealed class ActionContext
{
    private static readonly IReadOnlyDictionary<string, string> NoParameters = new Dictionary<string, string>();

    public ActionContext(IServiceProvider services, IReadOnlyDictionary<string, string>? parameters = null, string? reason = null)
    {
        Services = services;
        Parameters = parameters ?? NoParameters;
        Reason = reason;
    }

    public IServiceProvider Services { get; }

    /// <summary>Параметры из интерфейса и из <c>PrepareAsync</c>.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Причина, введённая в диалоге <see cref="ActionDanger.ConfirmWithReason"/>.</summary>
    public string? Reason { get; }

    /// <summary>Подробности для записи журнала («shadow без запроса»). Действие может задать в <c>ExecuteAsync</c>.</summary>
    public string? AuditDetails { get; set; }

    public IDomainConnection Connection => Services.GetRequiredService<ICurrentConnection>().Require();
    public IDialogService Dialogs => Services.GetRequiredService<IDialogService>();
    public IProcessLauncher Launcher => Services.GetRequiredService<IProcessLauncher>();
    public INotificationService Notifications => Services.GetRequiredService<INotificationService>();
    public ISessionService Sessions => Services.GetRequiredService<ISessionService>();
    public IUserDirectory Users => Services.GetRequiredService<IUserDirectory>();
    public IComputerDirectory Computers => Services.GetRequiredService<IComputerDirectory>();
    public IHostProbe Probe => Services.GetRequiredService<IHostProbe>();

    public bool GetFlag(string name) =>
        Parameters.TryGetValue(name, out var v) && bool.TryParse(v, out var b) && b;

    public string? GetParameter(string name) => Parameters.TryGetValue(name, out var v) ? v : null;
}

public sealed record BulkProgress(int Done, int Total, string? Current);

public sealed record TargetResult(string Target, ActionResult Result);

public sealed record BulkResult(IReadOnlyList<TargetResult> Results, bool Cancelled)
{
    public int Total => Results.Count;
    public int Succeeded => Results.Count(r => r.Result.IsOk);
    public int Failed => Results.Count(r => r.Result.Status == ActionStatus.Failed);
    public bool AllOk => !Cancelled && Failed == 0 && Results.All(r => r.Result.IsOk);

    /// <summary>«7 из 8 успешно». Для одной цели — её сообщение.</summary>
    public string Summary
    {
        get
        {
            if (Cancelled && Total == 0) return "Отменено";
            if (Total == 1)
            {
                var r = Results[0].Result;
                return r.Message ?? (r.IsOk ? "Готово" : r.Status == ActionStatus.Cancelled ? "Отменено" : "Не удалось");
            }

            return $"{Succeeded} из {Total} успешно";
        }
    }
}

/// <summary>
/// Запускает действия. Порядок: отфильтровать по <c>CanExecute</c> → <c>PrepareAsync</c> (один раз) →
/// подтверждение по максимальной <c>GetDanger</c> → выполнение (для пачки параллельно, не больше
/// <see cref="AppSettings.BulkParallelism"/>) → запись в журнал для каждой цели → итог. Исключения превращает в
/// <see cref="ActionResult.Failed"/>; <see cref="OperationFailedException"/> показывает как есть.
/// </summary>
public interface IActionRunner
{
    /// <summary>Все действия для типа цели, отсортированные по группе и порядку.</summary>
    IReadOnlyList<IAction<TTarget>> GetActions<TTarget>() where TTarget : IActionTarget;

    /// <summary>Контекст для проверки <c>CanExecute</c> при отрисовке кнопок.</summary>
    ActionContext CreateContext(IReadOnlyDictionary<string, string>? parameters = null);

    Task<BulkResult> RunAsync<TTarget>(
        IAction<TTarget> action,
        IReadOnlyList<TTarget> targets,
        IReadOnlyDictionary<string, string>? parameters,
        IProgress<BulkProgress>? progress,
        CancellationToken ct) where TTarget : IActionTarget;
}

public interface IModule
{
    string Id { get; }
    string Name { get; }

    /// <summary>Регистрирует действия, страницы и сервисы модуля.</summary>
    void Register(IServiceCollection services);
}

/// <summary>Раздел в левой навигации. Содержимое страницы подбирает оболочка (App) по <see cref="Id"/>.</summary>
public interface IPage
{
    string Id { get; }
    string Title { get; }

    /// <summary>Имя значка Tabler (ti-...) без префикса, например «device-desktop».</summary>
    string Icon { get; }

    int Order { get; }
}

public sealed record PageInfo(string Id, string Title, string Icon, int Order) : IPage;

public static class ActionServiceCollectionExtensions
{
    public static IServiceCollection AddAction<TTarget, TAction>(this IServiceCollection services)
        where TTarget : IActionTarget
        where TAction : class, IAction<TTarget>
    {
        services.AddSingleton<IAction<TTarget>, TAction>();
        return services;
    }

    public static IServiceCollection AddPage(this IServiceCollection services, IPage page)
    {
        services.AddSingleton(page);
        return services;
    }
}
