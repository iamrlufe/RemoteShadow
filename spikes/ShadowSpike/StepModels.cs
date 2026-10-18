namespace ShadowSpike;

internal enum StepStatus { Pass, Fail, Skip }

internal sealed record StepResult(
    string Id,
    string Name,
    StepStatus Status,
    long ElapsedMs,
    string Summary,
    IReadOnlyList<string> Details);

/// <summary>Всё, что нужно для отчёта. Секретов здесь нет; на всякий случай список <see cref="Secrets"/> вычищается из текста.</summary>
internal sealed record ReportData(
    DateTime GeneratedAt,
    string CommandLine,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    IReadOnlyList<StepResult> Steps,
    IReadOnlySet<string> Triggers,
    IReadOnlyList<string> Secrets);
