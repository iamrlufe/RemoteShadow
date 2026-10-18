using RemoteShadow.Core.Models;

namespace RemoteShadow.Demo;

/// <summary>
/// Элементы отчёта демо-диагностики: те же идентификаторы, заголовки, статусы и главный вывод, что у настоящей
/// CIM-диагностики (RemoteShadow.Windows.Hosts), чтобы в демо интерфейс показывал то же самое. Всё чистое.
/// </summary>
internal static class DemoDiagnosticItems
{
    public const string Dns = "dns";
    public const string Port445 = "port-445";
    public const string TermService = "termservice";
    public const string ShadowPolicy = "shadow-policy";
    public const string ShadowFirewall = "shadow-firewall";
    public const string AllowRemoteRpc = "allow-remote-rpc";

    public const string StartTermServiceFixId = "start-termservice";

    private const string ShadowFirewallDisplayName = "Remote Desktop - Shadow (TCP-In)";

    private const string PolicyLocation =
        "Конфигурация компьютера → Административные шаблоны → Компоненты Windows → Службы удалённых рабочих столов → " +
        "Узел сеансов удалённых рабочих столов → Подключения → Задать правила удалённого управления сеансами пользователей RDS";

    private const string ConsentHint =
        "Пользователю придёт запрос на подключение. Режим «без запроса» не сработает: для него нужно значение политики 2 (управление) или 4 (просмотр).";

    public const string WinRmHint =
        "Включите WinRM на компьютере (групповой политикой или командой winrm quickconfig) и откройте порт 5985 в брандмауэре.";

    /// <summary>Порты, которые проверяет диагностика, в порядке показа.</summary>
    public static readonly IReadOnlyList<int> ProbedPorts = [135, 445, 3389, 5985];

    public static string TitleOf(string id) => id switch
    {
        Dns => "Разрешение имени (DNS)",
        "port-135" => "Порт 135 (RPC)",
        Port445 => "Порт 445 (SMB, сессии и shadow)",
        "port-3389" => "Порт 3389 (RDP)",
        "port-5985" => "Порт 5985 (WinRM)",
        TermService => "Служба удалённых рабочих столов (TermService)",
        ShadowPolicy => "Политика shadow",
        ShadowFirewall => "Правило брандмауэра для shadow",
        AllowRemoteRpc => "Удалённое управление сессиями (AllowRemoteRPC)",
        _ => id,
    };

    public static string PortId(int port) => $"port-{port}";

    /// <summary>Проверка не выполнена: статус Unknown с пояснением.</summary>
    public static DiagnosticItem Unknown(string id, string reason, string? hint = null) =>
        new(id, TitleOf(id), DiagnosticStatus.Unknown, $"Не удалось проверить: {reason}.", hint);

    /// <summary>Все проверки, которые идут через WinRM, как «не удалось проверить» с одной причиной.</summary>
    public static IEnumerable<DiagnosticItem> AllCimUnknown(string reason, string? hint = null) =>
        new[] { TermService, ShadowPolicy, ShadowFirewall, AllowRemoteRpc }.Select(id => Unknown(id, reason, hint));

    public static DiagnosticItem DnsResolved(string host, string address) =>
        new(Dns, TitleOf(Dns), DiagnosticStatus.Ok, $"{host} → {address}.");

    public static DiagnosticItem DnsFailed(string host) =>
        new(Dns, TitleOf(Dns), DiagnosticStatus.Fail, $"Имя {host} не разрешается.",
            "Проверьте имя компьютера и настройки DNS. Если вы работаете не из сети организации, подключитесь по VPN — DNS-сервер должен знать доменные имена.");

    /// <summary>445 нужен для сессий и shadow — закрытый порт это Fail; 135, 3389 и 5985 — Warning.</summary>
    public static DiagnosticItem Port(int port, bool open)
    {
        var id = PortId(port);
        var title = TitleOf(id);
        if (open) return new DiagnosticItem(id, title, DiagnosticStatus.Ok, $"Порт {port} открыт.");

        return port switch
        {
            445 => new DiagnosticItem(id, title, DiagnosticStatus.Fail,
                "Порт 445 закрыт или компьютер не отвечает.",
                "Порт 445 нужен для списка сессий и shadow. Проверьте, что компьютер включён и сеть/брандмауэр пропускают 445 (правило «Общий доступ к файлам и принтерам (SMB — входящий)»)."),
            3389 => new DiagnosticItem(id, title, DiagnosticStatus.Warning,
                "Порт 3389 закрыт.",
                "RDP-подключение к компьютеру невозможно. На shadow это не влияет, но проверьте, включён ли удалённый рабочий стол и открыт ли порт в брандмауэре."),
            5985 => new DiagnosticItem(id, title, DiagnosticStatus.Warning,
                "Порт 5985 закрыт: WinRM недоступен.",
                "Без WinRM нельзя проверить службу, политику и брандмауэр на компьютере. Включите WinRM групповой политикой и откройте порт 5985."),
            135 => new DiagnosticItem(id, title, DiagnosticStatus.Warning,
                "Порт 135 закрыт.",
                "Порт 135 нужен службе сопоставления конечных точек RPC; часть удалённых вызовов может не работать."),
            _ => new DiagnosticItem(id, title, DiagnosticStatus.Warning, $"Порт {port} закрыт."),
        };
    }

    /// <summary>Остановленная служба — Fail с кнопкой «Исправить».</summary>
    public static DiagnosticItem TermServiceItem(bool running) => running
        ? new DiagnosticItem(TermService, TitleOf(TermService), DiagnosticStatus.Ok, "Служба запущена.")
        : new DiagnosticItem(TermService, TitleOf(TermService), DiagnosticStatus.Fail,
            "Служба не запущена (состояние: Stopped, тип запуска: вручную).",
            "Запустите службу кнопкой «Исправить».",
            StartTermServiceFixId);

    /// <summary>Политика Shadow: 0 — запрещено (Fail), 1 — с согласием (Ok с подсказкой), 2 — без запроса (Ok). Кнопки «Исправить» нет: политикой управляет GPO.</summary>
    public static DiagnosticItem ShadowPolicyItem(uint value) => value switch
    {
        0 => new DiagnosticItem(ShadowPolicy, TitleOf(ShadowPolicy), DiagnosticStatus.Fail,
            "Shadow запрещён политикой (Shadow = 0).", $"Измените групповую политику: {PolicyLocation}."),
        1 => new DiagnosticItem(ShadowPolicy, TitleOf(ShadowPolicy), DiagnosticStatus.Ok,
            "Shadow разрешён: управление сессией с согласия пользователя (Shadow = 1).", ConsentHint),
        _ => new DiagnosticItem(ShadowPolicy, TitleOf(ShadowPolicy), DiagnosticStatus.Ok,
            "Shadow разрешён: управление сессией без запроса пользователю (Shadow = 2)."),
    };

    public static DiagnosticItem ShadowFirewallOk() =>
        new(ShadowFirewall, TitleOf(ShadowFirewall), DiagnosticStatus.Ok, $"Правило «{ShadowFirewallDisplayName}» включено.");

    public static DiagnosticItem AllowRemoteRpcOk() =>
        new(AllowRemoteRpc, TitleOf(AllowRemoteRpc), DiagnosticStatus.Ok, "AllowRemoteRPC = 1: удалённое управление сессиями разрешено.");

    /// <summary>
    /// Главный вывод одной строкой. Приоритет: DNS → порт 445 → служба TermService → политика Shadow → брандмауэр →
    /// прочие сбои → «часть проверок не выполнена» → предупреждения → «всё в порядке».
    /// </summary>
    public static string Verdict(IReadOnlyList<DiagnosticItem> items)
    {
        if (Has(items, Dns, DiagnosticStatus.Fail))
            return "Shadow недоступен: имя компьютера не разрешается в DNS";

        if (Has(items, Port445, DiagnosticStatus.Fail))
            return "Shadow заблокирован: порт 445 закрыт";

        if (Has(items, TermService, DiagnosticStatus.Fail))
            return "Shadow недоступен: не запущена служба удалённых рабочих столов (TermService)";

        if (Has(items, ShadowPolicy, DiagnosticStatus.Fail))
            return "Shadow заблокирован политикой";

        if (Has(items, ShadowFirewall, DiagnosticStatus.Fail))
            return $"Shadow заблокирован: выключено правило брандмауэра «{ShadowFirewallDisplayName}»";

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
}
