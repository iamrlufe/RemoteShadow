using System.Net;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows.Hosts;

/// <summary>
/// Диагностика «почему не работает shadow»: DNS, порты, а при открытом WinRM — служба TermService, политика Shadow
/// и правило брандмауэра через CIM. Невозможность проверки не бросает исключение, а даёт элемент со статусом Unknown.
/// </summary>
public sealed class CimDiagnosticsService : IDiagnosticsService
{
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(3);

    private static readonly int[] ProbedPorts = [135, 445, 3389, 5985];

    private readonly ICurrentConnection _connection;
    private readonly IHostProbe _probe;
    private readonly CimRemoteFactory _cimFactory;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly TimeSpan _cimDeadline;

    public CimDiagnosticsService(ICurrentConnection connection, IHostProbe probe)
        : this(
            connection,
            probe,
            CimRemote.Connect,
            static (host, ct) => Dns.GetHostAddressesAsync(host, ct),
            TimeSpan.FromSeconds(30))
    {
    }

    internal CimDiagnosticsService(
        ICurrentConnection connection,
        IHostProbe probe,
        CimRemoteFactory cimFactory,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        TimeSpan cimDeadline)
    {
        _connection = connection;
        _probe = probe;
        _cimFactory = cimFactory;
        _resolve = resolve;
        _cimDeadline = cimDeadline;
    }

    public async Task<DiagnosticReport> RunAsync(ComputerInfo computer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var host = HostNameValidator.Require(computer.HostName);
        var connection = _connection.Require();

        var items = new List<DiagnosticItem>();

        var dns = await CheckDnsAsync(host, ct).ConfigureAwait(false);
        items.Add(dns);
        if (dns.Status == DiagnosticStatus.Fail)
        {
            const string reason = "имя компьютера не разрешается";
            items.AddRange(ProbedPorts.Select(p => DiagnosticItems.PortSkipped(p, reason)));
            items.AddRange(DiagnosticItems.AllCimUnknown(reason));
            return DiagnosticReportBuilder.Build(host, items, DateTime.UtcNow);
        }

        var portTasks = ProbedPorts.Select(port => CheckPortAsync(host, port, ct)).ToArray();
        var ports = await Task.WhenAll(portTasks).ConfigureAwait(false);
        items.AddRange(ports.Select(p => p.Item));

        var winRmOpen = ports.Any(p => p.Port == 5985 && p.Open);
        if (winRmOpen)
            items.AddRange(await RunCimChecksAsync(host, connection.Credential, ct).ConfigureAwait(false));
        else
            items.AddRange(DiagnosticItems.WinRmUnavailable());

        return DiagnosticReportBuilder.Build(host, items, DateTime.UtcNow);
    }

    public async Task FixAsync(ComputerInfo computer, string fixId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computer);
        if (fixId != DiagnosticFixIds.StartTermService)
        {
            throw new OperationFailedException(
                "Это исправление не поддерживается: программа меняет только то, что не управляется групповой политикой.",
                OperationFailure.InvalidInput);
        }

        var host = HostNameValidator.Require(computer.HostName);
        var credential = _connection.Require().Credential;
        var failureText = $"Не удалось запустить службу TermService на компьютере {host}";

        uint code;
        try
        {
            code = await RunBlockingAsync(
                () =>
                {
                    using var remote = _cimFactory(host, credential);
                    return remote.StartService(DiagnosticItems.TermServiceName);
                },
                ct).ConfigureAwait(false);
        }
        catch (CimRemoteException ex)
        {
            throw new OperationFailedException($"{failureText}: {ex.Message}.", ToFailure(ex.Kind), ex);
        }
        catch (TimeoutException ex)
        {
            throw new OperationFailedException(
                $"{failureText}: компьютер не ответил по WinRM за {_cimDeadline.TotalSeconds:0} с.",
                OperationFailure.Timeout,
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OperationFailedException($"{failureText}: {UnexpectedText(ex)}.", OperationFailure.Other, ex);
        }

        if (!ServiceReturnCodes.IsSuccess(code))
        {
            var failure = code == 2 ? OperationFailure.AccessDenied : OperationFailure.Other;
            throw new OperationFailedException($"{failureText}: {ServiceReturnCodes.Describe(code)} (код {code}).", failure);
        }
    }

    private async Task<DiagnosticItem> CheckDnsAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return DiagnosticItems.DnsResolved(host, [literal]);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DnsTimeout);
        try
        {
            var addresses = await _resolve(host, cts.Token).ConfigureAwait(false);
            return addresses.Length == 0
                ? DiagnosticItems.DnsFailed(host, "адреса не найдены")
                : DiagnosticItems.DnsResolved(host, addresses);
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            var reason = ex is OperationCanceledException ? "нет ответа от DNS-сервера" : null;
            return DiagnosticItems.DnsFailed(host, reason);
        }
    }

    private async Task<(int Port, bool Open, DiagnosticItem Item)> CheckPortAsync(string host, int port, CancellationToken ct)
    {
        var open = await _probe.IsPortOpenAsync(host, port, PortTimeout, ct).ConfigureAwait(false);
        return (port, open, DiagnosticItems.Port(port, open));
    }

    private async Task<IReadOnlyList<DiagnosticItem>> RunCimChecksAsync(string host, AccountCredential? credential, CancellationToken ct)
    {
        try
        {
            return await RunBlockingAsync(() => RunCimChecks(host, credential, ct), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return DiagnosticItems.AllCimUnknown($"компьютер не ответил по WinRM за {_cimDeadline.TotalSeconds:0} с").ToList();
        }
    }

    /// <summary>Выполняется на потоке пула .NET; единый дедлайн накладывает <see cref="RunBlockingAsync{T}"/>.</summary>
    private IReadOnlyList<DiagnosticItem> RunCimChecks(string host, AccountCredential? credential, CancellationToken ct)
    {
        ICimRemote remote;
        try
        {
            remote = _cimFactory(host, credential);
        }
        catch (CimRemoteException ex)
        {
            return DiagnosticItems.AllCimUnknown(ex.Message).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DiagnosticItems.AllCimUnknown(UnexpectedText(ex)).ToList();
        }

        using (remote)
        {
            var items = new List<DiagnosticItem>();
            string? fatalReason = null;

            void Check(string id, Func<DiagnosticItem> check)
            {
                if (fatalReason is not null)
                {
                    items.Add(DiagnosticItems.Unknown(id, fatalReason));
                    return;
                }

                ct.ThrowIfCancellationRequested();
                try
                {
                    items.Add(check());
                }
                catch (CimRemoteException ex)
                {
                    items.Add(DiagnosticItems.Unknown(id, ex.Message));
                    if (ex.IsFatal) fatalReason = ex.Message;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Сбой разбора ответа или самой библиотеки: отчёт должен выйти, а не оборваться исключением.
                    items.Add(DiagnosticItems.Unknown(id, UnexpectedText(ex)));
                }
            }

            Check(DiagnosticIds.TermService,
                () => DiagnosticItems.TermService(remote.GetService(DiagnosticItems.TermServiceName)));
            Check(DiagnosticIds.ShadowPolicy,
                () => DiagnosticItems.ShadowPolicy(
                    remote.ReadHklmDword(ShadowPolicyInterpreter.RegistrySubKey, ShadowPolicyInterpreter.RegistryValueName)));
            Check(DiagnosticIds.ShadowFirewall,
                () => DiagnosticItems.ShadowFirewall(
                    remote.IsFirewallRuleEnabled(DiagnosticItems.ShadowFirewallRuleName, DiagnosticItems.ShadowFirewallDisplayName)));
            Check(DiagnosticIds.AllowRemoteRpc,
                () => DiagnosticItems.AllowRemoteRpc(
                    remote.ReadHklmDword(DiagnosticItems.TerminalServerSubKey, DiagnosticItems.AllowRemoteRpcValueName)));

            return items;
        }
    }

    /// <summary>
    /// Блокирующий CIM-вызов на потоке пула .NET с общим дедлайном. Не уложились — <see cref="TimeoutException"/>,
    /// а вызов дорабатывает в фоне (у каждой CIM-операции свой таймаут) и сам закрывает сессию.
    /// </summary>
    private Task<T> RunBlockingAsync<T>(Func<T> work, CancellationToken ct) =>
        Task.Run(work, CancellationToken.None).WaitAsync(_cimDeadline, ct);

    /// <summary>Текст для непредвиденного сбоя: без стека и без точки в конце (её ставит вызывающий).</summary>
    private static string UnexpectedText(Exception ex) =>
        $"непредвиденная ошибка ({CimHelpers.ShortMessage(ex.Message).TrimEnd('.')})";

    private static OperationFailure ToFailure(CimFailureKind kind) => kind switch
    {
        CimFailureKind.AccessDenied => OperationFailure.AccessDenied,
        CimFailureKind.Connection => OperationFailure.Unreachable,
        _ => OperationFailure.Other,
    };
}
