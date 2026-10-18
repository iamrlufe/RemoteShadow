using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace ShadowSpike;

/// <summary>Проверки фазы 0. Каждая — отдельный шаг; пароль хранится только в поле и уходит только в LogonRequest.</summary>
internal sealed partial class SpikeChecks
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);

    private readonly SpikeOptions _options;
    private readonly IServiceProvider _sp;
    private readonly ICurrentConnection _current;
    private readonly StepRunner _runner;
    private readonly string? _password;

    private DomainEnvironmentInfo? _environment;
    private string? _domain;

    public SpikeChecks(SpikeOptions options, IServiceProvider sp, StepRunner runner, string? password)
    {
        _options = options;
        _sp = sp;
        _current = sp.GetRequiredService<ICurrentConnection>();
        _runner = runner;
        _password = password;
    }

    public List<KeyValuePair<string, string>> EnvironmentFacts { get; } = [];

    private bool LoggedIn => _current.Current is not null;

    public async Task RunAllAsync()
    {
        await _runner.RunAsync("1", "Окружение", EnvironmentAsync);
        await _runner.RunAsync("2", "Вход в домен", LogonAsync);
        await _runner.RunAsync("3", "DNS SRV", DnsSrvAsync);

        if (LoggedIn)
        {
            await _runner.RunAsync("4", "Компьютеры (LDAP)", ComputersAsync);
            await _runner.RunAsync("5", "Пользователи (LDAP)", UsersAsync);
        }
        else
        {
            await _runner.SkipAsync("4", "Компьютеры (LDAP)", "нет входа в домен (шаг 2)");
            await _runner.SkipAsync("5", "Пользователи (LDAP)", "нет входа в домен (шаг 2)");
        }

        await SessionStepAsync("6a", "Сессии: --host", _options.Host, expectFailure: false);
        await SessionStepAsync("6b", "Сессии: выключенный (--offline-host)", _options.OfflineHost, expectFailure: true);
        await SessionStepAsync("6c", "Сессии: закрыт 445 (--closed-port-host)", _options.ClosedPortHost, expectFailure: true);

        if (_options.Host is null)
        {
            await _runner.SkipAsync("7", "Онлайн-проверка портов", "не задан --host");
            await _runner.SkipAsync("8", "Диагностика (CIM)", "не задан --host");
        }
        else
        {
            await _runner.RunAsync("7", "Онлайн-проверка портов", ProbeAsync);
            if (LoggedIn) await _runner.RunAsync("8", "Диагностика (CIM)", DiagnosticsAsync);
            else await _runner.SkipAsync("8", "Диагностика (CIM)", "нет входа в домен (шаг 2)");
        }

        if (_options.ShadowSession is { } sid)
        {
            await LaunchStepAsync("9a", "mstsc: shadow", MstscArgs.Shadow(_options.Host!, sid, _options.NoConsent), isShadow: true);
        }
        else
        {
            await _runner.SkipAsync("9a", "mstsc: shadow", "не задан --shadow-session");
        }

        if (_options.Rdp)
            await LaunchStepAsync("9b", "mstsc: обычный RDP", MstscArgs.Rdp(_options.Host!), isShadow: false);
        else
            await _runner.SkipAsync("9b", "mstsc: обычный RDP", "не задан --rdp");
    }

    // ---------------------------------------------------------------- 1. Окружение

    private async Task EnvironmentAsync(StepLog log)
    {
        void Fact(string key, string value)
        {
            EnvironmentFacts.Add(new(key, value));
            log.Info($"{key}: {value}");
        }

        var logon = _sp.GetRequiredService<IDomainLogonService>();
        _environment = logon.DetectEnvironment();
        Fact("Компьютер", _environment.ComputerName);
        Fact("Пользователь Windows", _environment.CurrentUser);
        Fact("В домене", _environment.IsDomainJoined ? "да" : "нет");
        Fact("Домен компьютера", _environment.DomainName ?? "(нет)");

        Fact("Windows", WindowsVersion());
        Fact(".NET", RuntimeInformation.FrameworkDescription);
        Fact("Процесс", $"{RuntimeInformation.ProcessArchitecture}, {(IsElevated() ? "с правами администратора" : "без повышения прав")}");

        var launcher = _sp.GetRequiredService<IProcessLauncher>();
        Fact("mstsc.exe", launcher.FindExecutable("mstsc.exe") ?? "НЕ НАЙДЕН");
        Fact("ssh.exe", launcher.FindExecutable("ssh.exe") ?? "не найден");

        var failed = false;

        // seclogon
        var start = ReadRegistry(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\seclogon", "Start");
        var startText = start is int s
            ? s switch { 2 => "автоматически", 3 => "вручную (по требованию)", 4 => "ОТКЛЮЧЕНА", _ => $"тип {s}" }
            : "служба не найдена в реестре";
        var state = await QueryServiceStateAsync("seclogon");
        Fact("Служба seclogon", $"состояние: {state}; запуск: {startText}");
        if (start is null || (start is int s4 && s4 == 4))
        {
            failed = true;
            log.Trigger("seclogon");
            log.Warn("seclogon отключена или отсутствует: запуск mstsc под другими учётными данными (netonly) не сработает.");
        }
        else if (state != "RUNNING")
        {
            log.Info("seclogon не запущена, но не отключена: Windows запускает её по требованию. Если запуск mstsc упадёт с ошибкой службы, см. раздел «Что делать, если…».");
        }

        // Credential Guard
        var dgKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
        var vbs = ReadRegistry(Registry.LocalMachine, dgKey, "EnableVirtualizationBasedSecurity");
        var dgFlags = ReadRegistry(Registry.LocalMachine, dgKey, "LsaCfgFlags");
        var lsaFlags = ReadRegistry(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags");
        var cgScenario = ReadRegistry(Registry.LocalMachine, dgKey + @"\Scenarios\CredentialGuard", "Enabled");
        var flags = new[] { dgFlags, lsaFlags }.OfType<int>().FirstOrDefault(f => f != 0);
        var cgOn = flags is 1 or 2 || (cgScenario is int e && e == 1);
        Fact("Credential Guard (по реестру)",
            $"{(cgOn ? "ВКЛЮЧЁН/настроен" : "не настроен")}; LsaCfgFlags: DeviceGuard={Show(dgFlags)}, Lsa={Show(lsaFlags)}; VBS={Show(vbs)}; сценарий CredentialGuard={Show(cgScenario)}");
        if (cgOn)
        {
            log.Trigger("credguard");
            log.Warn("Credential Guard запрещает передачу учётных данных по умолчанию: mstsc может спрашивать пароль (реестр показывает настройку, а не факт работы).");
        }

        // Политика делегирования
        ReadDelegationPolicy(log, Fact);

        if (failed) log.Fail("seclogon отключена или отсутствует");
        else log.Pass($"{(_environment.IsDomainJoined ? "компьютер в домене " + _environment.DomainName : "компьютер вне домена")}; seclogon: {state}; Credential Guard: {(cgOn ? "да" : "нет")}");
    }

    private static void ReadDelegationPolicy(StepLog log, Action<string, string> fact)
    {
        const string path = @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null)
            {
                fact("Политика «Разрешить передачу учётных данных по умолчанию»", "не задана (ключ отсутствует)");
                log.Trigger("credguard");
                return;
            }

            var allow = key.GetValue("AllowDefaultCredentials");
            var concat = key.GetValue("ConcatenateDefaults_AllowDefault");
            var list = new List<string>();
            using (var sub = key.OpenSubKey("AllowDefaultCredentials"))
            {
                if (sub is not null)
                {
                    foreach (var name in sub.GetValueNames())
                        list.Add(Convert.ToString(sub.GetValue(name), CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }

            fact("Политика «Разрешить передачу учётных данных по умолчанию»",
                $"AllowDefaultCredentials={Show(allow)}, Concatenate={Show(concat)}, серверы: {(list.Count == 0 ? "(список пуст)" : string.Join("; ", list))}");
            if (allow is not int a || a != 1 || list.Count == 0) log.Trigger("credguard");
        }
        catch (Exception ex)
        {
            fact("Политика «Разрешить передачу учётных данных по умолчанию»", $"не удалось прочитать ({ex.GetType().Name})");
        }
    }

    private static async Task<string> QueryServiceStateAsync(string service)
    {
        try
        {
            var result = await ProcessTool.RunAsync("sc.exe", ["query", service], TimeSpan.FromSeconds(10));
            if (result.TimedOut) return "sc.exe не ответил";
            var m = ServiceStateRegex().Match(result.Output);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            return result.ExitCode == 1060 ? "служба не установлена" : $"не удалось разобрать вывод sc.exe (код {result.ExitCode})";
        }
        catch (Exception ex)
        {
            return $"sc.exe не запустился ({ex.GetType().Name})";
        }
    }

    [GeneratedRegex(@"\d\s+(RUNNING|STOPPED|START_PENDING|STOP_PENDING|PAUSED|CONTINUE_PENDING|PAUSE_PENDING)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceStateRegex();

    private static string WindowsVersion()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = ReadRegistry(Registry.LocalMachine, key, "ProductName");
        var display = ReadRegistry(Registry.LocalMachine, key, "DisplayVersion");
        var build = ReadRegistry(Registry.LocalMachine, key, "CurrentBuildNumber");
        var ubr = ReadRegistry(Registry.LocalMachine, key, "UBR");
        return $"{product ?? "Windows"} {display}, сборка {build}.{ubr} (ядро {Environment.OSVersion.Version})";
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static object? ReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Show(object? value) => value is null ? "—" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—";

    // ---------------------------------------------------------------- 2. Вход

    private async Task LogonAsync(StepLog log)
    {
        _domain = _options.Domain ?? _environment?.DomainName;
        var explicitMode = _options.UserName is not null;
        if (!explicitMode && !_options.CurrentUser && _environment?.IsDomainJoined != true)
        {
            log.Fail("не задан --user, а компьютер вне домена: войти текущим пользователем нельзя");
            return;
        }

        var mode = explicitMode ? LogonMode.Explicit : LogonMode.CurrentUser;
        if (string.IsNullOrWhiteSpace(_domain))
        {
            log.Trigger("dns");
            log.Fail("домен не задан: укажите --domain");
            return;
        }

        if (explicitMode && string.IsNullOrEmpty(_password))
        {
            log.Fail("пароль не задан (консоль без ввода и нет SPIKE_PASSWORD)");
            return;
        }

        log.Info($"Режим: {(mode == LogonMode.Explicit ? "явные учётные данные, логин " + _options.UserName : "текущий пользователь")}; домен {_domain}; контроллер: {_options.DomainController ?? "автопоиск"}");

        var logon = _sp.GetRequiredService<IDomainLogonService>();
        using var cts = new CancellationTokenSource(StepTimeout);
        var result = await logon.LogonAsync(
            new LogonRequest(mode, _domain, _options.DomainController, explicitMode ? _options.UserName : null, explicitMode ? _password : null),
            cts.Token);

        if (!result.Success || result.Connection is null)
        {
            switch (result.Error)
            {
                case LogonError.DnsFailure: log.Trigger("dns"); break;
                case LogonError.DomainControllerUnreachable: log.Trigger("dc"); break;
                default: log.Trigger("ldap-auth"); break;
            }

            log.Fail($"{result.Error}: {result.Message}");
            return;
        }

        var c = result.Connection;
        _current.Set(c);
        log.Info($"AccountName: {c.AccountName}");
        log.Info($"DomainName: {c.DomainName}");
        log.Info($"DomainController: {c.DomainController ?? "(не определён)"}");
        log.Info($"AuthenticationKind: {c.AuthenticationKind}");
        log.Info($"UsesExplicitCredentials: {c.UsesExplicitCredentials}");
        log.Info($"IsPrivileged: {result.IsPrivilegedAccount}");
        if (result.IsPrivilegedAccount)
            log.Warn("учётная запись привилегированная (Domain Admins и т. п.): программа не должна сохранять её пароль.");
        log.Pass($"{c.AccountName}, контроллер {c.DomainController ?? "?"}, {c.AuthenticationKind}, привилегированная: {(result.IsPrivilegedAccount ? "да" : "нет")}");
    }

    // ---------------------------------------------------------------- 3. DNS SRV

    private async Task DnsSrvAsync(StepLog log)
    {
        if (string.IsNullOrWhiteSpace(_domain))
        {
            log.Skip("домен неизвестен");
            return;
        }

        if (!DomainNameRegex().IsMatch(_domain))
        {
            log.Skip("имя домена содержит недопустимые символы");
            return;
        }

        // Сервис Windows ищет контроллеры через DnsQuery_W внутри входа и список наружу не отдаёт, поэтому
        // проверяем DNS независимо: nslookup (SRV) и обычное разрешение имени.
        var query = "_ldap._tcp.dc._msdcs." + _domain.TrimEnd('.');
        var found = new List<string>();
        try
        {
            var result = await ProcessTool.RunAsync("nslookup.exe", ["-type=SRV", query], TimeSpan.FromSeconds(20));
            if (result.TimedOut)
            {
                log.Info("nslookup не ответил за 20 с.");
            }
            else
            {
                var suffix = Regex.Escape(_domain.TrimEnd('.'));
                foreach (Match m in Regex.Matches(result.Output, $@"(?<![\w.\-])([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.{suffix})(?![\w\-])", RegexOptions.IgnoreCase))
                {
                    var name = m.Groups[1].Value;
                    if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (!found.Contains(name, StringComparer.OrdinalIgnoreCase)) found.Add(name);
                }

                log.Info("Вывод nslookup:");
                foreach (var line in result.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(40))
                    log.Info("  " + line);
            }
        }
        catch (Exception ex)
        {
            log.Info($"nslookup не запущен: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(_domain.TrimEnd('.'));
            log.Info($"Имя {_domain} разрешается в: {string.Join(", ", addresses.Select(a => a.ToString()))} ({sw.ElapsedMilliseconds} мс)");
        }
        catch (Exception ex)
        {
            log.Info($"Имя {_domain} не разрешается: {ex.Message}");
        }

        if (_options.DomainController is { } dc)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(dc);
                log.Info($"Контроллер {dc}: {string.Join(", ", addresses.Select(a => a.ToString()))}");
            }
            catch (Exception ex)
            {
                log.Info($"Контроллер {dc} не разрешается: {ex.Message}");
            }
        }

        var filtered = found.Where(n => !n.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filtered.Count > 0)
        {
            log.Pass($"найдено контроллеров: {filtered.Count} ({string.Join(", ", filtered.Take(5))})");
        }
        else
        {
            log.Trigger("dns");
            log.Fail($"SRV-записи {query} не найдены");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$")]
    private static partial Regex DomainNameRegex();

    // ---------------------------------------------------------------- 4. Компьютеры

    private async Task ComputersAsync(StepLog log)
    {
        var dir = _sp.GetRequiredService<IComputerDirectory>();
        using var cts = new CancellationTokenSource(StepTimeout);

        var root = await dir.GetRootDnAsync(cts.Token);
        log.Info($"Корень домена: {root}; контроллер каталога: {dir.DomainController ?? "?"}");

        var sw = Stopwatch.StartNew();
        var count = 0;
        var noDns = 0;
        var first = new List<string>();
        await foreach (var c in dir.EnumerateAsync(new ComputerQuery(), cts.Token))
        {
            count++;
            if (string.IsNullOrWhiteSpace(c.DnsName)) noDns++;
            if (first.Count < 5) first.Add($"{c.Name} ({c.HostName}) {c.OperatingSystem}");
        }

        var loadMs = sw.ElapsedMilliseconds;
        log.Info($"Включённые компьютеры: {count} за {loadMs} мс; без dNSHostName: {noDns}");
        foreach (var f in first) log.Info("  " + f);

        sw.Restart();
        var all = 0;
        var disabled = 0;
        await foreach (var c in dir.EnumerateAsync(new ComputerQuery(IncludeDisabled: true), cts.Token))
        {
            all++;
            if (!c.Enabled) disabled++;
        }

        log.Info($"С отключёнными (IncludeDisabled): {all} за {sw.ElapsedMilliseconds} мс, из них отключено: {disabled}");

        sw.Restart();
        var changed = 0;
        await foreach (var _ in dir.EnumerateAsync(new ComputerQuery(DateTime.UtcNow.AddDays(-1)), cts.Token))
            changed++;
        log.Info($"Изменённые за сутки (ChangedSince): {changed} за {sw.ElapsedMilliseconds} мс");

        sw.Restart();
        var ous = await dir.GetOuChildrenAsync(root, cts.Token);
        log.Info($"Дочерние узлы корня: {ous.Count} за {sw.ElapsedMilliseconds} мс");
        foreach (var ou in ous.Take(30)) log.Info($"  {ou.Name} [{(ou.HasChildren ? "есть вложенные" : "пусто")}] {ou.DistinguishedName}");

        if (count == 0) log.Warn("список компьютеров пуст — проверьте права на чтение и корень поиска.");
        log.Pass($"{count} компьютеров за {loadMs} мс, отключённых {disabled}, изменённых за сутки {changed}, узлов в корне {ous.Count}");
    }

    // ---------------------------------------------------------------- 5. Пользователи

    private async Task UsersAsync(StepLog log)
    {
        var dir = _sp.GetRequiredService<IUserDirectory>();
        using var cts = new CancellationTokenSource(StepTimeout);
        var sw = Stopwatch.StartNew();
        var users = await dir.SearchAsync(_options.FindUser, 50, cts.Token);
        log.Info($"Поиск «{_options.FindUser}»: {users.Count} (максимум 50) за {sw.ElapsedMilliseconds} мс");
        foreach (var u in users.Take(3))
        {
            log.Info($"  {u.ShownName} ({u.SamAccountName}): Enabled={u.Enabled}, LockedOut={u.LockedOut}, " +
                     $"PasswordExpires={(u.PasswordNeverExpires ? "никогда" : u.PasswordExpires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "неизвестно")}");
        }

        if (users.Count == 0) log.Warn("ничего не найдено; задайте другую строку через --find-user.");
        log.Pass($"найдено {users.Count} за {sw.ElapsedMilliseconds} мс (операции записи не выполнялись)");
    }

    // ---------------------------------------------------------------- 6. Сессии

    private async Task SessionStepAsync(string id, string name, string? host, bool expectFailure)
    {
        if (host is null)
        {
            await _runner.SkipAsync(id, name, "параметр не задан");
            return;
        }

        if (!LoggedIn)
        {
            await _runner.SkipAsync(id, name, "нет входа в домен (шаг 2)");
            return;
        }

        await _runner.RunAsync(id, $"{name}: {host}", async log =>
        {
            var svc = _sp.GetRequiredService<ISessionService>();
            using var cts = new CancellationTokenSource(StepTimeout);
            var sw = Stopwatch.StartNew();
            var result = await svc.GetSessionsAsync(host, cts.Token);
            var ms = sw.ElapsedMilliseconds;
            log.Info($"Статус: {result.Status}; время до ответа: {ms} мс; сообщение: {result.Message ?? "—"}");
            foreach (var s in result.Sessions)
            {
                log.Info($"  сессия {s.Id}: {s.State}, {(s.HasUser ? s.AccountName : "(нет пользователя)")}, станция {s.WinStationName ?? "—"}, " +
                         $"клиент {s.ClientName ?? "—"}, вход {s.LogonTime?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—"}, " +
                         $"простой {s.IdleTime?.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture) ?? "—"}, " +
                         $"shadow: {(s.CanShadow ? "можно" : s.ShadowBlockedReason)}");
            }

            var candidate = result.Sessions.FirstOrDefault(s => s.CanShadow);
            if (!expectFailure && candidate is not null && _options.ShadowSession is null)
                log.Info($"Для шага 9 можно указать --shadow-session {candidate.Id} ({candidate.AccountName}).");

            if (expectFailure)
            {
                if (result.Status is HostQueryStatus.Unreachable or HostQueryStatus.Timeout)
                {
                    if (ms > 15_000) log.Warn($"ответ пришёл только через {ms} мс — для интерфейса это долго.");
                    log.Pass($"{result.Status} за {ms} мс (ожидалось Unreachable/Timeout)");
                }
                else
                {
                    log.Fail($"получен статус {result.Status} за {ms} мс, ожидался Unreachable/Timeout");
                }

                return;
            }

            if (result.IsOk)
            {
                log.Pass($"{result.Sessions.Count} сессий за {ms} мс");
            }
            else
            {
                if (result.Status is HostQueryStatus.Unreachable or HostQueryStatus.Timeout) log.Trigger("port445");
                log.Fail($"{result.Status} за {ms} мс: {result.Message}");
            }
        });
    }

    // ---------------------------------------------------------------- 7. Порты

    private async Task ProbeAsync(StepLog log)
    {
        var probe = _sp.GetRequiredService<IHostProbe>();
        var host = _options.Host!;
        using var cts = new CancellationTokenSource(StepTimeout);
        var parts = new List<string>();
        var open445 = false;
        foreach (var port in new[] { 135, 445, 3389, 5985 })
        {
            var sw = Stopwatch.StartNew();
            var open = await probe.IsPortOpenAsync(host, port, TimeSpan.FromSeconds(3), cts.Token);
            log.Info($"Порт {port}: {(open ? "открыт" : "закрыт/нет ответа")} ({sw.ElapsedMilliseconds} мс)");
            parts.Add($"{port}={(open ? "да" : "нет")}");
            if (port == 445) open445 = open;
        }

        var sw2 = Stopwatch.StartNew();
        var online = await probe.IsOnlineAsync(host, TimeSpan.FromSeconds(3), cts.Token);
        log.Info($"IsOnlineAsync: {online} ({sw2.ElapsedMilliseconds} мс)");

        if (open445)
        {
            log.Pass($"порты: {string.Join(", ", parts)}");
        }
        else
        {
            log.Trigger("port445");
            log.Fail($"порт 445 недоступен ({string.Join(", ", parts)})");
        }
    }

    // ---------------------------------------------------------------- 8. Диагностика

    private async Task DiagnosticsAsync(StepLog log)
    {
        var diag = _sp.GetRequiredService<IDiagnosticsService>();
        var host = _options.Host!;
        var shortName = host.Split('.')[0];
        var computer = new ComputerInfo
        {
            Id = Guid.NewGuid(),
            Name = shortName,
            DnsName = host.Contains('.') ? host : null,
            DistinguishedName = $"CN={shortName}",
        };

        using var cts = new CancellationTokenSource(StepTimeout);
        var report = await diag.RunAsync(computer, cts.Token);
        foreach (var item in report.Items)
        {
            log.Info($"[{item.Status}] {item.Id}: {item.Title} — {item.Detail}{(item.Hint is null ? string.Empty : " | Что сделать: " + item.Hint)}");
            if (item.Status != DiagnosticStatus.Ok)
            {
                switch (item.Id)
                {
                    case "shadow-policy": log.Trigger("shadow-policy"); break;
                    case "dns": log.Trigger("dns"); break;
                    case "port-445": log.Trigger("port445"); break;
                }
            }
        }

        log.Info($"Вердикт: {report.Verdict ?? "—"}");
        var fails = report.Items.Count(i => i.Status == DiagnosticStatus.Fail);
        var unknown = report.Items.Count(i => i.Status == DiagnosticStatus.Unknown);
        if (fails > 0) log.Fail($"проблем: {fails}, не удалось проверить: {unknown}. Вердикт: {report.Verdict ?? "—"}");
        else log.Pass($"критичных проблем нет, не удалось проверить: {unknown}. Вердикт: {report.Verdict ?? "—"}");
    }

    // ---------------------------------------------------------------- 9. mstsc

    private async Task LaunchStepAsync(string id, string name, IReadOnlyList<string> args, bool isShadow)
    {
        if (!LoggedIn)
        {
            await _runner.SkipAsync(id, name, "нет входа в домен (шаг 2)");
            return;
        }

        await _runner.RunAsync(id, name, async log =>
        {
            var launcher = _sp.GetRequiredService<IProcessLauncher>();
            var exe = launcher.FindExecutable("mstsc.exe");
            if (exe is null)
            {
                log.Fail("mstsc.exe не найден");
                return;
            }

            var mode = _current.Current!.UsesExplicitCredentials ? "netonly (CreateProcessWithLogonW)" : "обычный запуск (текущий пользователь)";
            Console.WriteLine($"    Будет запущено: mstsc.exe {string.Join(' ', args)}");
            Console.WriteLine($"    Режим: {mode}");
            if (!ConsoleIo.AskYesNo("    Запустить mstsc? (y/n)"))
            {
                log.Skip("отказ пользователя");
                return;
            }

            using var cts = new CancellationTokenSource(StepTimeout);
            var sw = Stopwatch.StartNew();
            await launcher.StartAsync("mstsc.exe", args, cts.Token);
            log.Info($"Процесс запущен за {sw.ElapsedMilliseconds} мс: mstsc.exe {string.Join(' ', args)}; режим: {mode}");

            Console.WriteLine("    Дождитесь результата в окне mstsc, затем ответьте на вопросы.");
            var password = ConsoleIo.AskYesNo("    Окно mstsc спросило пароль? (y/n)");
            var connected = ConsoleIo.AskYesNo(isShadow ? "    Подключился ли shadow? (y/n)" : "    Подключился ли RDP-сеанс? (y/n)");
            log.Info($"Ответ: окно спросило пароль: {(password ? "да" : "нет")}");
            log.Info($"Ответ: {(isShadow ? "shadow" : "RDP")} подключился: {(connected ? "да" : "нет")}");

            var consent = false;
            if (isShadow)
            {
                consent = ConsoleIo.AskYesNo(_options.NoConsent
                    ? "    Увидел ли пользователь запрос согласия? (y/n) (с --no-consent не должен был)"
                    : "    Увидел ли пользователь запрос согласия? (y/n)");
                log.Info($"Ответ: запрос согласия показан пользователю: {(consent ? "да" : "нет")}");
                if (_options.NoConsent && consent)
                {
                    log.Trigger("shadow-policy");
                    log.Warn("запрос согласия появился несмотря на /noConsentPrompt: политика Shadow на целевом компьютере не равна 2 или 4.");
                }
            }

            if (password)
            {
                log.Trigger(isShadow ? "shadow-netonly" : "credguard");
                log.Warn("mstsc спросил пароль: учётные данные netonly не применились автоматически.");
            }

            if (!connected)
            {
                if (isShadow) log.Trigger("shadow-netonly");
                log.Fail(isShadow ? "shadow не подключился" : "RDP не подключился");
            }
            else
            {
                log.Pass($"подключился; пароль спрашивали: {(password ? "да" : "нет")}{(isShadow ? $"; согласие показано: {(consent ? "да" : "нет")}" : string.Empty)}");
            }
        });
    }
}

/// <summary>Аргументы mstsc для шага 9. Совпадают с действиями Shadow/RDP в основной программе.</summary>
internal static class MstscArgs
{
    public static IReadOnlyList<string> Shadow(string host, int sessionId, bool noConsent)
    {
        var args = new List<string>
        {
            $"/v:{host}",
            $"/shadow:{sessionId.ToString(CultureInfo.InvariantCulture)}",
            "/control",
        };
        if (noConsent) args.Add("/noConsentPrompt");
        return args;
    }

    public static IReadOnlyList<string> Rdp(string host) => [$"/v:{host}"];
}
