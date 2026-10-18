namespace RemoteShadow.Core.Models;

public enum DiagnosticStatus
{
    Ok,
    Warning,
    Fail,

    /// <summary>Не удалось проверить (например, нет WinRM).</summary>
    Unknown,
}

/// <param name="Id">Стабильный идентификатор проверки: dns, port-445, termservice, shadow-policy.</param>
/// <param name="Hint">Что исправить. Для настроек из GPO — какую политику поменять.</param>
/// <param name="FixId">Если не null, проверку можно исправить кнопкой «Исправить» (только то, что не управляется GPO).</param>
public sealed record DiagnosticItem(
    string Id,
    string Title,
    DiagnosticStatus Status,
    string Detail,
    string? Hint = null,
    string? FixId = null);

public sealed record DiagnosticReport(
    string Host,
    IReadOnlyList<DiagnosticItem> Items,
    DateTime CreatedUtc)
{
    public bool HasProblems => Items.Any(i => i.Status is DiagnosticStatus.Fail or DiagnosticStatus.Warning);

    /// <summary>Главный вывод одной строкой: «Shadow заблокирован политикой».</summary>
    public string? Verdict { get; init; }
}
