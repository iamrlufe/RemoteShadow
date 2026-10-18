using RemoteShadow.Core.Models;

namespace RemoteShadow.Core.Services;

public interface IAuditLog
{
    /// <summary>Дописывает запись. Не должна бросать исключения: сбой журнала не отменяет действие.</summary>
    Task WriteAsync(AuditRecord record, CancellationToken ct = default);

    /// <summary>Последние записи, новые первыми.</summary>
    Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int max, DateTime? sinceUtc = null, CancellationToken ct = default);
}

public sealed record ConfirmRequest(
    string Title,
    string Message,
    IReadOnlyList<string> Targets,
    bool IsDangerous = false);

public sealed record ReasonResult(bool Confirmed, string? Reason);

public sealed record PasswordChoice(string Password, bool MustChangeAtNextLogon);

/// <summary>Диалоги оболочки. Реализует App.</summary>
public interface IDialogService
{
    /// <summary>Да/Нет с перечнем целей.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>Да/Нет с обязательным полем «Причина», которое попадёт в журнал.</summary>
    Task<ReasonResult> ConfirmWithReasonAsync(ConfirmRequest request);

    /// <summary>Однострочный или многострочный ввод. null — пользователь отменил.</summary>
    Task<string?> PromptTextAsync(string title, string message, string? initial = null, bool multiline = false);

    /// <summary>Сброс пароля: ввести или сгенерировать, «сменить при следующем входе». null — отмена.</summary>
    Task<PasswordChoice?> PromptPasswordResetAsync(string userLabel);

    /// <summary>Выбор группы из найденных. null — отмена.</summary>
    Task<GroupInfo?> PickGroupAsync(string title, Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>> search);

    Task ShowErrorAsync(string title, string message);
    Task ShowInfoAsync(string title, string message);
}

public interface INotificationService
{
    void Info(string message);
    void Success(string message);
    void Error(string message);
}
