using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Как читать значение политики Shadow: статус, описание и подсказка.</summary>
internal readonly record struct ShadowPolicyOutcome(DiagnosticStatus Status, string Detail, string? Hint);

/// <summary>
/// Правила интерпретации DWORD «Shadow» из HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services:
/// 0 — запрещено, 1 — управление с согласием, 2 — управление без согласия, 3 — просмотр с согласием, 4 — просмотр без согласия.
/// Не задано — действует режим 1. Значением управляет GPO, поэтому «исправить» его кнопкой нельзя.
/// </summary>
internal static class ShadowPolicyInterpreter
{
    public const string RegistrySubKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    public const string RegistryValueName = "Shadow";

    public const string PolicyLocation =
        "Конфигурация компьютера → Административные шаблоны → Компоненты Windows → Службы удалённых рабочих столов → " +
        "Узел сеансов удалённых рабочих столов → Подключения → Задать правила удалённого управления сеансами пользователей RDS";

    private const string ConsentHint =
        "Пользователю придёт запрос на подключение. Режим «без запроса» не сработает: для него нужно значение политики 2 (управление) или 4 (просмотр).";

    private const string ViewOnlyHint = "Политика разрешает только просмотр: режим управления недоступен.";

    /// <param name="value">Значение DWORD или null, если политика не задана.</param>
    public static ShadowPolicyOutcome Interpret(uint? value) => value switch
    {
        null => new(
            DiagnosticStatus.Ok,
            "Политика не задана: действует режим по умолчанию — управление сессией с согласия пользователя.",
            ConsentHint),
        0 => new(
            DiagnosticStatus.Fail,
            "Shadow запрещён политикой (Shadow = 0).",
            $"Измените групповую политику: {PolicyLocation}."),
        1 => new(
            DiagnosticStatus.Ok,
            "Shadow разрешён: управление сессией с согласия пользователя (Shadow = 1).",
            ConsentHint),
        2 => new(
            DiagnosticStatus.Ok,
            "Shadow разрешён: управление сессией без запроса пользователю (Shadow = 2).",
            null),
        3 => new(
            DiagnosticStatus.Ok,
            "Shadow разрешён: только просмотр с согласия пользователя (Shadow = 3).",
            $"{ConsentHint} {ViewOnlyHint}"),
        4 => new(
            DiagnosticStatus.Ok,
            "Shadow разрешён: только просмотр без запроса пользователю (Shadow = 4).",
            ViewOnlyHint),
        _ => new(
            DiagnosticStatus.Warning,
            $"Нестандартное значение политики Shadow = {value}.",
            $"Проверьте групповую политику: {PolicyLocation}."),
    };
}
