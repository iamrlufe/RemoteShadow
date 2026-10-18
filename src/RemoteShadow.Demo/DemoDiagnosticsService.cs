using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>Что «не так» с компьютером в демо-диагностике. Выбирается по имени компьютера.</summary>
public enum DemoDiagnosticScenario
{
    /// <summary>Всё в порядке (Shadow = 2).</summary>
    Healthy,

    /// <summary>Политика Shadow = 1: подключиться можно только с согласия пользователя; проверка Ok, но с подсказкой.</summary>
    ShadowNeedsConsent,

    /// <summary>Политика Shadow = 0: shadow запрещён групповой политикой.</summary>
    ShadowForbidden,

    /// <summary>Порт 445 закрыт: остальное проверить нельзя. Так выглядит выключенный компьютер.</summary>
    Port445Closed,

    /// <summary>Служба TermService остановлена; исправляется кнопкой (FixId start-termservice).</summary>
    TermServiceStopped,

    /// <summary>В AD нет DNS-имени: имя не разрешается.</summary>
    NoDnsRecord,

    /// <summary>Порт 5985 закрыт: реестр и службы проверить нельзя.</summary>
    WinRmUnavailable,
}

/// <summary>
/// Тестовая диагностика. Отчёт повторяет по составу и выводам настоящую CIM-диагностику и зависит только
/// от имени компьютера (и от того, исправили ли службу), поэтому у одного компьютера он всегда одинаковый.
/// Если компьютер в <see cref="DemoData"/> «не в сети», его отчёт — «порт 445 закрыт»; «в сети» с закрытым 445 не бывает:
/// иначе диагностика противоречила бы списку компьютеров.
/// </summary>
public sealed class DemoDiagnosticsService : IDiagnosticsService
{
    public const string StartTermServiceFixId = DemoDiagnosticItems.StartTermServiceFixId;

    private readonly DemoData _data;
    private readonly DemoTimings _timings;
    private readonly object _gate = new();
    private readonly HashSet<string> _termServiceStarted = new(StringComparer.OrdinalIgnoreCase);

    public DemoDiagnosticsService(DemoData data, DemoTimings? timings = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _timings = timings ?? DemoTimings.Default;
    }

    /// <summary>
    /// Сценарий по имени: 50% — всё хорошо, 15% — Shadow = 1, 5% — Shadow = 0, 10% — закрыт 445, 10% — остановлен TermService,
    /// 5% — нет DNS-записи, 5% — недоступен WinRM. Регистр имени не важен.
    /// </summary>
    public static DemoDiagnosticScenario GetScenario(string computerName)
    {
        var bucket = DemoRandom.Hash(computerName ?? string.Empty) % 100;
        return bucket switch
        {
            < 50 => DemoDiagnosticScenario.Healthy,
            < 65 => DemoDiagnosticScenario.ShadowNeedsConsent,
            < 70 => DemoDiagnosticScenario.ShadowForbidden,
            < 80 => DemoDiagnosticScenario.Port445Closed,
            < 90 => DemoDiagnosticScenario.TermServiceStopped,
            < 95 => DemoDiagnosticScenario.NoDnsRecord,
            _ => DemoDiagnosticScenario.WinRmUnavailable,
        };
    }

    public async Task<DiagnosticReport> RunAsync(ComputerInfo computer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computer);

        await DemoDelay.Wait(DemoDelay.ByKey(computer.Name, _timings.DiagnosticsMin, _timings.DiagnosticsMax), ct).ConfigureAwait(false);

        var scenario = ResolveScenario(computer);
        bool started;
        lock (_gate) started = _termServiceStarted.Contains(computer.Name);

        return BuildReport(computer, scenario, started);
    }

    /// <summary>Поддерживается <see cref="StartTermServiceFixId"/>: «запускает» службу в памяти.</summary>
    public async Task FixAsync(ComputerInfo computer, string fixId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computer);

        if (fixId != StartTermServiceFixId)
        {
            throw new OperationFailedException(
                "Это исправление не поддерживается: программа меняет только то, что не управляется групповой политикой.",
                OperationFailure.InvalidInput);
        }

        var known = _data.FindComputerByHost(computer.Name);
        if (known is not null && !_data.IsOnline(known.Id))
        {
            throw new OperationFailedException(
                $"Не удалось запустить службу TermService на компьютере {computer.Name}: компьютер недоступен (1722).",
                OperationFailure.Unreachable);
        }

        await DemoDelay.Wait(_timings.ActionDelay, ct).ConfigureAwait(false);
        lock (_gate) _termServiceStarted.Add(computer.Name);
    }

    private DemoDiagnosticScenario ResolveScenario(ComputerInfo computer)
    {
        // Нет dNSHostName в AD — это и есть проблема; а если имя есть, «нет DNS-записи» быть не может.
        if (string.IsNullOrWhiteSpace(computer.DnsName)) return DemoDiagnosticScenario.NoDnsRecord;

        var scenario = GetScenario(computer.Name);
        var known = _data.FindComputerByHost(computer.Name);
        if (known is not null)
        {
            if (!_data.IsOnline(known.Id)) return DemoDiagnosticScenario.Port445Closed;
            if (scenario == DemoDiagnosticScenario.Port445Closed) scenario = DemoDiagnosticScenario.Healthy;
        }

        return scenario == DemoDiagnosticScenario.NoDnsRecord ? DemoDiagnosticScenario.Healthy : scenario;
    }

    private static DiagnosticReport BuildReport(ComputerInfo computer, DemoDiagnosticScenario scenario, bool termServiceStarted)
    {
        var host = computer.HostName;
        var items = new List<DiagnosticItem>();

        if (scenario == DemoDiagnosticScenario.NoDnsRecord)
        {
            const string reason = "имя компьютера не разрешается";
            items.Add(DemoDiagnosticItems.DnsFailed(host));
            items.AddRange(DemoDiagnosticItems.ProbedPorts.Select(p => DemoDiagnosticItems.Unknown(DemoDiagnosticItems.PortId(p), reason)));
            items.AddRange(DemoDiagnosticItems.AllCimUnknown(reason));
            return Finish(host, items);
        }

        var hash = DemoRandom.Hash(computer.Name);
        var address = $"10.{10 + hash % 40}.{(hash >> 8) % 250 + 1}.{(hash >> 16) % 250 + 2}";
        items.Add(DemoDiagnosticItems.DnsResolved(host, address));

        var winRmOpen = scenario is not (DemoDiagnosticScenario.Port445Closed or DemoDiagnosticScenario.WinRmUnavailable);
        foreach (var port in DemoDiagnosticItems.ProbedPorts)
        {
            var open = scenario != DemoDiagnosticScenario.Port445Closed && (port != 5985 || winRmOpen);
            items.Add(DemoDiagnosticItems.Port(port, open));
        }

        if (!winRmOpen)
        {
            items.AddRange(DemoDiagnosticItems.AllCimUnknown("WinRM (5985) недоступен", DemoDiagnosticItems.WinRmHint));
            return Finish(host, items);
        }

        var termServiceRunning = scenario != DemoDiagnosticScenario.TermServiceStopped || termServiceStarted;
        items.Add(DemoDiagnosticItems.TermServiceItem(termServiceRunning));
        items.Add(DemoDiagnosticItems.ShadowPolicyItem(scenario switch
        {
            DemoDiagnosticScenario.ShadowForbidden => 0u,
            DemoDiagnosticScenario.ShadowNeedsConsent => 1u,
            _ => 2u,
        }));
        items.Add(DemoDiagnosticItems.ShadowFirewallOk());
        items.Add(DemoDiagnosticItems.AllowRemoteRpcOk());
        return Finish(host, items);
    }

    private static DiagnosticReport Finish(string host, List<DiagnosticItem> items) =>
        new(host, items, DateTime.UtcNow) { Verdict = DemoDiagnosticItems.Verdict(items) };
}
