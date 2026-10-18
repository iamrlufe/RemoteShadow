using System.Text;

namespace ShadowSpike;

/// <summary>Построение Markdown-отчёта и консольной таблицы. Чистые функции без доступа к консоли и файлам.</summary>
internal static class ReportBuilder
{
    private sealed record Hint(string Trigger, string Title, string[] Lines);

    private static readonly Hint[] Hints =
    [
        new("shadow-netonly", "Shadow не берёт учётные данные netonly (mstsc спрашивает пароль или не подключается)",
        [
            "Проверьте, что запущена служба seclogon и нет Credential Guard (см. шаг 1).",
            "Запасной вариант: запускать `mstsc /v:<хост> /shadow:<id> /control /prompt` — mstsc сам спросит учётные данные.",
            "Второй вариант: добавить в политику клиента «Разрешить передачу учётных данных по умолчанию» (Computer Configuration > Administrative Templates > System > Credentials Delegation) запись TERMSRV/<хост> или TERMSRV/*.corp.local.",
            "`cmdkey` с паролем работает, но пишет пароль в Диспетчер учётных данных — по умолчанию не используем.",
            "Зафиксируйте итог в docs/research/spike.md и выберите вариант до фазы 1.",
        ]),
        new("seclogon", "Служба seclogon («Вторичный вход в систему») остановлена или отключена",
        [
            "Запуск: `sc.exe start seclogon` от администратора. Тип запуска по умолчанию «Вручную» — служба стартует по требованию, это нормально.",
            "Если тип «Отключена» — вернуть «Вручную» (`sc.exe config seclogon start= demand`) или проверить GPO, которая её отключает.",
            "Без seclogon CreateProcessWithLogonW не работает: придётся запускать программу целиком под другой учётной записью.",
        ]),
        new("dns", "DNS не разрешает имя домена или контроллера",
        [
            "С недоменного компьютера DNS-сервер должен указывать на контроллер домена (или должен быть поднят VPN с доменным DNS).",
            "Проверьте: `nslookup -type=SRV _ldap._tcp.dc._msdcs.<домен>`.",
            "Временный обход: указать контроллер в `--dc` по FQDN и прописать его в hosts. По IP не подключаться: Kerberos не работает, NTLM в защищённых доменах запрещён.",
        ]),
        new("dc", "Контроллер домена недоступен",
        [
            "Проверьте порты 389 (LDAP), 88 (Kerberos), 53 (DNS) до контроллера, брандмауэр, VPN.",
            "Попробуйте указать другой контроллер через `--dc`.",
        ]),
        new("ldap-auth", "LDAP не принимает учётные данные",
        [
            "Проверьте формат логина: `adm-ivanov`, `CORP\\adm-ivanov` или `adm-ivanov@corp.local`.",
            "Учётная запись могла быть заблокирована после неверных попыток: не повторяйте вход подряд.",
            "Если контроллер требует LDAPS вместо подписи и шифрования Negotiate, это нужно учесть в реализации.",
        ]),
        new("port445", "Порт 445 закрыт или хост недоступен",
        [
            "WTS-вызовы (список сессий, shadow) идут по RPC поверх SMB (445). Разрешите 445/TCP во входящем брандмауэре на клиенте («File and Printer Sharing (SMB-In)»).",
            "Для закрытого порта сервис должен вернуть Unreachable/Timeout быстро; если время ответа велико (десятки секунд), запишите его в итоги и скорректируйте дедлайны.",
            "Выключенный компьютер ведёт себя иначе, чем компьютер с закрытым 445: сравните времена в шаге 6.",
        ]),
        new("shadow-policy", "Политика Shadow не разрешает подключение без запроса",
        [
            "Политика: Computer Configuration > Administrative Templates > Windows Components > Remote Desktop Services > Remote Desktop Session Host > Connections > «Set rules for remote control of Remote Desktop Services user sessions».",
            "Значения Shadow в реестре (HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services): 1 — полный доступ с разрешением пользователя, 2 — полный доступ без разрешения, 3 — просмотр с разрешением, 4 — просмотр без разрешения, 0 — запрещено.",
            "Для `/noConsentPrompt` нужно 2 (управление) или 4 (просмотр). При 1 и 3 пользователь увидит запрос согласия — это штатно.",
            "Правило брандмауэра «Remote Desktop - Shadow (TCP-In)» должно быть включено, служба TermService запущена.",
        ]),
        new("credguard", "Credential Guard или политика передачи учётных данных",
        [
            "Credential Guard запрещает передачу учётных данных по умолчанию: mstsc будет спрашивать пароль даже при netonly-запуске.",
            "Если политика «Разрешить передачу учётных данных по умолчанию» не задана, CredSSP не передаст пароль без запроса.",
            "Либо принять запрос пароля, либо добавить TERMSRV/* домена в политику (при отключённом Credential Guard).",
        ]),
    ];

    public static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            if (secret.Length >= 3) text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text;
    }

    public static string StatusText(StepStatus status) => status switch
    {
        StepStatus.Pass => "PASS",
        StepStatus.Fail => "FAIL",
        _ => "SKIP",
    };

    /// <summary>Таблица для консоли: шаг, результат, время, пояснение.</summary>
    public static string BuildConsoleTable(IReadOnlyList<StepResult> steps)
    {
        var sb = new StringBuilder();
        var nameWidth = Math.Min(40, Math.Max(10, steps.Count == 0 ? 10 : steps.Max(s => s.Name.Length)));
        sb.AppendLine($"{"Шаг",-4} {"Проверка".PadRight(nameWidth)} {"Итог",-5} {"мс",8}  Пояснение");
        foreach (var s in steps)
        {
            var name = s.Name.Length > nameWidth ? s.Name[..nameWidth] : s.Name;
            sb.AppendLine($"{s.Id,-4} {name.PadRight(nameWidth)} {StatusText(s.Status),-5} {s.ElapsedMs,8}  {OneLine(s.Summary)}");
        }

        sb.AppendLine(Totals(steps));
        return sb.ToString();
    }

    public static string BuildMarkdown(ReportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Отчёт ShadowSpike");
        sb.AppendLine();
        sb.AppendLine($"Дата: {data.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"Параметры запуска: `{data.CommandLine}`");
        sb.AppendLine();
        sb.AppendLine("Пароль в отчёт не попадает.");
        sb.AppendLine();

        sb.AppendLine("## Окружение");
        sb.AppendLine();
        if (data.Environment.Count == 0) sb.AppendLine("Данные не собраны.");
        foreach (var kv in data.Environment) sb.AppendLine($"- {kv.Key}: {kv.Value}");
        sb.AppendLine();

        sb.AppendLine("## Результаты");
        sb.AppendLine();
        sb.AppendLine("| Шаг | Проверка | Итог | мс | Пояснение |");
        sb.AppendLine("|---|---|---|---:|---|");
        foreach (var s in data.Steps)
            sb.AppendLine($"| {Cell(s.Id)} | {Cell(s.Name)} | {StatusText(s.Status)} | {s.ElapsedMs} | {Cell(OneLine(s.Summary))} |");
        sb.AppendLine();
        sb.AppendLine(Totals(data.Steps));

        sb.AppendLine();
        sb.AppendLine("## Подробности");
        foreach (var s in data.Steps)
        {
            sb.AppendLine();
            sb.AppendLine($"### Шаг {s.Id}. {s.Name} — {StatusText(s.Status)} ({s.ElapsedMs} мс)");
            sb.AppendLine();
            sb.AppendLine(s.Summary);
            if (s.Details.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("```text");
                foreach (var line in s.Details) sb.AppendLine(line);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Что делать, если…");
        sb.AppendLine();
        sb.AppendLine("Пункты с пометкой «СРАБОТАЛО» относятся к проблемам, найденным в этом запуске.");
        foreach (var h in Hints)
        {
            var hit = data.Triggers.Contains(h.Trigger);
            sb.AppendLine();
            sb.AppendLine($"### {(hit ? "[СРАБОТАЛО] " : string.Empty)}{h.Title}");
            sb.AppendLine();
            foreach (var line in h.Lines) sb.AppendLine($"- {line}");
        }

        return Redact(sb.ToString(), data.Secrets);
    }

    private static string Totals(IReadOnlyList<StepResult> steps) =>
        $"Итого: PASS {steps.Count(s => s.Status == StepStatus.Pass)}, FAIL {steps.Count(s => s.Status == StepStatus.Fail)}, SKIP {steps.Count(s => s.Status == StepStatus.Skip)}.";

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

    private static string Cell(string text) => text.Replace("|", "\\|");
}
