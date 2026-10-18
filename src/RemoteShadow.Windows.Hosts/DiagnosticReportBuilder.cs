using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Собирает отчёт: расставляет проверки в порядке показа и формулирует главный вывод.</summary>
internal static class DiagnosticReportBuilder
{
    public static DiagnosticReport Build(string host, IEnumerable<DiagnosticItem> items, DateTime createdUtc)
    {
        var ordered = Order(items);
        return new DiagnosticReport(host, ordered, createdUtc) { Verdict = Verdict(ordered) };
    }

    /// <summary>Порядок по <see cref="DiagnosticIds.DisplayOrder"/>; незнакомые проверки — в конце, в исходном порядке.</summary>
    public static IReadOnlyList<DiagnosticItem> Order(IEnumerable<DiagnosticItem> items) =>
        items
            .Select((item, index) => (item, index, rank: Rank(item.Id)))
            .OrderBy(x => x.rank)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

    /// <summary>
    /// Главный вывод одной строкой. Приоритет: DNS → порт 445 → служба TermService → политика Shadow → брандмауэр →
    /// прочие сбои → «часть проверок не выполнена» → предупреждения → «всё в порядке». Побеждает первая сработавшая причина:
    /// без имени и порта 445 остальные проверки не имеют смысла.
    /// </summary>
    public static string Verdict(IReadOnlyList<DiagnosticItem> items)
    {
        if (Has(items, DiagnosticIds.Dns, DiagnosticStatus.Fail))
            return "Shadow недоступен: имя компьютера не разрешается в DNS";

        if (Has(items, DiagnosticIds.Port445, DiagnosticStatus.Fail))
            return "Shadow заблокирован: порт 445 закрыт";

        if (Has(items, DiagnosticIds.TermService, DiagnosticStatus.Fail))
            return "Shadow недоступен: не запущена служба удалённых рабочих столов (TermService)";

        if (Has(items, DiagnosticIds.ShadowPolicy, DiagnosticStatus.Fail))
            return "Shadow заблокирован политикой";

        if (Has(items, DiagnosticIds.ShadowFirewall, DiagnosticStatus.Fail))
            return "Shadow заблокирован: выключено правило брандмауэра «Remote Desktop - Shadow (TCP-In)»";

        var otherFail = items.FirstOrDefault(i => i.Status == DiagnosticStatus.Fail);
        if (otherFail is not null)
            return $"Найдена проблема: {otherFail.Title}";

        if (items.Any(i => i.Status == DiagnosticStatus.Unknown))
            return "Явных причин блокировки shadow не найдено, но часть проверок выполнить не удалось";

        if (items.Any(i => i.Status == DiagnosticStatus.Warning))
            return "Явных причин блокировки shadow не найдено, есть предупреждения";

        return "Всё в порядке: причин блокировки shadow не найдено";
    }

    private static bool Has(IReadOnlyList<DiagnosticItem> items, string id, DiagnosticStatus status) =>
        items.Any(i => i.Id == id && i.Status == status);

    private static int Rank(string id)
    {
        for (var i = 0; i < DiagnosticIds.DisplayOrder.Count; i++)
            if (DiagnosticIds.DisplayOrder[i] == id) return i;
        return int.MaxValue;
    }
}
