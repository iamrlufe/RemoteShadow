using System.Net;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Стабильные идентификаторы проверок диагностики (<see cref="DiagnosticItem.Id"/>).</summary>
internal static class DiagnosticIds
{
    public const string Dns = "dns";
    public const string Port135 = "port-135";
    public const string Port445 = "port-445";
    public const string Port3389 = "port-3389";
    public const string Port5985 = "port-5985";
    public const string TermService = "termservice";
    public const string ShadowPolicy = "shadow-policy";
    public const string ShadowFirewall = "shadow-firewall";
    public const string AllowRemoteRpc = "allow-remote-rpc";

    /// <summary>Порядок показа проверок в отчёте.</summary>
    public static readonly IReadOnlyList<string> DisplayOrder =
    [
        Dns, Port135, Port445, Port3389, Port5985, TermService, ShadowPolicy, ShadowFirewall, AllowRemoteRpc,
    ];

    /// <summary>Проверки, которые выполняются через CIM (WinRM).</summary>
    public static readonly IReadOnlyList<string> CimChecks = [TermService, ShadowPolicy, ShadowFirewall, AllowRemoteRpc];
}

/// <summary>Идентификаторы исправлений (<see cref="DiagnosticItem.FixId"/>).</summary>
internal static class DiagnosticFixIds
{
    public const string StartTermService = "start-termservice";
}

/// <summary>Фабрика элементов отчёта. Всё чистое: тексты и статусы зависят только от аргументов.</summary>
internal static class DiagnosticItems
{
    public const string TermServiceName = "TermService";
    public const string ShadowFirewallRuleName = "RemoteDesktop-Shadow-In-TCP";
    public const string ShadowFirewallDisplayName = "Remote Desktop - Shadow (TCP-In)";
    public const string TerminalServerSubKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    public const string AllowRemoteRpcValueName = "AllowRemoteRPC";

    private const string WinRmUnavailableReason = "WinRM (5985) недоступен";

    private static readonly Dictionary<string, string> Titles = new()
    {
        [DiagnosticIds.Dns] = "Разрешение имени (DNS)",
        [DiagnosticIds.Port135] = "Порт 135 (RPC)",
        [DiagnosticIds.Port445] = "Порт 445 (SMB, сессии и shadow)",
        [DiagnosticIds.Port3389] = "Порт 3389 (RDP)",
        [DiagnosticIds.Port5985] = "Порт 5985 (WinRM)",
        [DiagnosticIds.TermService] = "Служба удалённых рабочих столов (TermService)",
        [DiagnosticIds.ShadowPolicy] = "Политика shadow",
        [DiagnosticIds.ShadowFirewall] = "Правило брандмауэра для shadow",
        [DiagnosticIds.AllowRemoteRpc] = "Удалённое управление сессиями (AllowRemoteRPC)",
    };

    public static string TitleOf(string id) => Titles.TryGetValue(id, out var title) ? title : id;

    public static string PortId(int port) => $"port-{port}";

    /// <summary>Проверка не выполнена: статус Unknown с пояснением «не удалось проверить: …».</summary>
    public static DiagnosticItem Unknown(string id, string reason, string? hint = null) =>
        new(id, TitleOf(id), DiagnosticStatus.Unknown, $"Не удалось проверить: {reason}.", hint);

    /// <summary>Все CIM-проверки как «не удалось проверить» с одной причиной.</summary>
    public static IEnumerable<DiagnosticItem> AllCimUnknown(string reason, string? hint = null) =>
        DiagnosticIds.CimChecks.Select(id => Unknown(id, reason, hint));

    public static IEnumerable<DiagnosticItem> WinRmUnavailable() =>
        AllCimUnknown(
            WinRmUnavailableReason,
            "Включите WinRM на компьютере (групповой политикой или командой winrm quickconfig) и откройте порт 5985 в брандмауэре.");

    public static DiagnosticItem DnsResolved(string host, IReadOnlyCollection<IPAddress> addresses) =>
        new(DiagnosticIds.Dns, TitleOf(DiagnosticIds.Dns), DiagnosticStatus.Ok,
            IPAddress.TryParse(host, out _)
                ? $"Указан IP-адрес {host}, разрешение имени не требуется."
                : $"{host} → {string.Join(", ", addresses.Take(3))}{(addresses.Count > 3 ? "…" : string.Empty)}.");

    public static DiagnosticItem DnsFailed(string host, string? reason = null) =>
        new(DiagnosticIds.Dns, TitleOf(DiagnosticIds.Dns), DiagnosticStatus.Fail,
            $"Имя {host} не разрешается{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})")}.",
            "Проверьте имя компьютера и настройки DNS. Если вы работаете не из сети организации, подключитесь по VPN — DNS-сервер должен знать доменные имена.");

    /// <summary>Порт не проверялся, потому что имя не разрешается.</summary>
    public static DiagnosticItem PortSkipped(int port, string reason) => Unknown(PortId(port), reason);

    /// <summary>
    /// Результат проверки порта. 445 нужен для сессий и shadow — закрытый порт это Fail;
    /// 135, 3389 и 5985 — Warning: без них часть возможностей недоступна, но shadow может работать.
    /// </summary>
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

    /// <summary>
    /// Служба TermService. Не запущена → Fail с кнопкой «Исправить»; если служба отключена, запустить её нельзя
    /// (нужно менять тип запуска), поэтому FixId не ставится.
    /// </summary>
    public static DiagnosticItem TermService(ServiceState? service)
    {
        var id = DiagnosticIds.TermService;
        var title = TitleOf(id);

        if (service is null)
        {
            return new DiagnosticItem(id, title, DiagnosticStatus.Fail,
                "Служба TermService не найдена на компьютере.",
                "На этой версии Windows службы удалённых рабочих столов нет или она удалена: shadow невозможен.");
        }

        if (string.Equals(service.State, "Running", StringComparison.OrdinalIgnoreCase))
            return new DiagnosticItem(id, title, DiagnosticStatus.Ok, "Служба запущена.");

        var disabled = string.Equals(service.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase);
        return new DiagnosticItem(id, title, DiagnosticStatus.Fail,
            $"Служба не запущена (состояние: {StateText(service.State)}, тип запуска: {StartModeText(service.StartMode)}).",
            disabled
                ? "Служба отключена. Включите её (тип запуска «Вручную» или «Автоматически») на компьютере или групповой политикой."
                : "Запустите службу кнопкой «Исправить».",
            disabled ? null : DiagnosticFixIds.StartTermService);
    }

    /// <summary>Политика Shadow. FixId не ставится: значением управляет GPO.</summary>
    public static DiagnosticItem ShadowPolicy(uint? value)
    {
        var outcome = ShadowPolicyInterpreter.Interpret(value);
        return new DiagnosticItem(DiagnosticIds.ShadowPolicy, TitleOf(DiagnosticIds.ShadowPolicy), outcome.Status, outcome.Detail, outcome.Hint);
    }

    /// <summary>Правило брандмауэра «Remote Desktop - Shadow (TCP-In)»: выключено или нет → Fail. FixId не ставится: правилом управляет GPO.</summary>
    public static DiagnosticItem ShadowFirewall(bool? ruleEnabled)
    {
        var id = DiagnosticIds.ShadowFirewall;
        var title = TitleOf(id);

        return ruleEnabled switch
        {
            true => new DiagnosticItem(id, title, DiagnosticStatus.Ok, $"Правило «{ShadowFirewallDisplayName}» включено."),
            false => new DiagnosticItem(id, title, DiagnosticStatus.Fail,
                $"Правило «{ShadowFirewallDisplayName}» выключено.",
                FirewallHint),
            null => new DiagnosticItem(id, title, DiagnosticStatus.Fail,
                $"Правило «{ShadowFirewallDisplayName}» не найдено.",
                FirewallHint),
        };
    }

    /// <summary>AllowRemoteRPC — информационная проверка: источники расходятся в том, нужен ли параметр для shadow.</summary>
    public static DiagnosticItem AllowRemoteRpc(uint? value)
    {
        var id = DiagnosticIds.AllowRemoteRpc;
        var title = TitleOf(id);

        if (value == 1)
            return new DiagnosticItem(id, title, DiagnosticStatus.Ok, "AllowRemoteRPC = 1: удалённое управление сессиями разрешено.");

        var current = value is null ? "не задан" : $"= {value}";
        return new DiagnosticItem(id, title, DiagnosticStatus.Warning,
            $"Параметр AllowRemoteRPC {current}.",
            "Справочно: по документации Microsoft источники расходятся, нужен ли он для shadow. Если список сессий и shadow работают, можно игнорировать.");
    }

    private const string FirewallHint =
        "Включите правило групповой политикой (Конфигурация компьютера → Конфигурация Windows → Параметры безопасности → " +
        "Брандмауэр Защитника Windows в режиме повышенной безопасности → Правила для входящих подключений). Правилом управляет GPO, поэтому программа его не меняет.";

    private static string StateText(string state) => string.IsNullOrWhiteSpace(state) ? "неизвестно" : state;

    private static string StartModeText(string mode) => mode.ToLowerInvariant() switch
    {
        "auto" => "автоматически",
        "manual" => "вручную",
        "disabled" => "отключена",
        "boot" or "system" => "системный",
        "" => "неизвестно",
        _ => mode,
    };
}

/// <summary>Расшифровка кодов возврата Win32_Service.StartService.</summary>
internal static class ServiceReturnCodes
{
    /// <summary>0 — запущена, 10 — уже запущена: в обоих случаях цель достигнута.</summary>
    public static bool IsSuccess(uint code) => code is 0 or 10;

    public static string Describe(uint code) => code switch
    {
        0 => "успешно",
        1 => "операция не поддерживается",
        2 => "доступ запрещён",
        3 => "есть зависимые службы",
        4 => "недопустимая команда службе",
        5 => "служба не может принять команду",
        6 => "служба не активна",
        7 => "время ожидания запроса к службе истекло",
        8 => "неизвестная ошибка",
        9 => "путь к файлу службы не найден",
        10 => "служба уже запущена",
        11 => "база данных служб заблокирована",
        12 => "зависимая служба удалена",
        13 => "не удалось запустить зависимую службу",
        14 => "служба отключена",
        15 => "ошибка входа службы в систему",
        16 => "служба помечена на удаление",
        17 => "у службы нет потока",
        18 => "циклическая зависимость служб",
        19 => "дублирующееся имя службы",
        20 => "недопустимое имя службы",
        21 => "недопустимый параметр",
        22 => "недопустимая учётная запись службы",
        23 => "служба уже существует",
        24 => "служба уже приостановлена",
        _ => "неизвестный код",
    };
}
