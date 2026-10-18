using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;
using RemoteShadow.Windows.Native;

namespace RemoteShadow.Windows;

/// <summary>
/// Вход в домен: проверка учётных данных пробным LDAP-подключением (Negotiate, подпись и шифрование).
/// Ожидаемые ошибки (неверный пароль, блокировка, недоступный контроллер) возвращаются в <see cref="LogonResult"/>, а не бросаются.
/// </summary>
public sealed class WindowsDomainLogonService : IDomainLogonService
{
    private static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    // Сколько найденных через DNS контроллеров пробовать, если первый недоступен.
    private const int MaxDiscoveredControllers = 3;

    public DomainEnvironmentInfo DetectEnvironment()
    {
        var machine = Environment.MachineName;
        var joined = false;
        string? netBios = null;

        try
        {
            if (NativeMethods.NetGetJoinInformation(null, out var buffer, out var status) == 0)
            {
                try
                {
                    joined = status == NativeMethods.NetSetupDomainName;
                    netBios = buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer);
                }
                finally
                {
                    if (buffer != IntPtr.Zero) NativeMethods.NetApiBufferFree(buffer);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Не Windows или урезанная система: считаем, что компьютер не в домене.
        }

        string? dnsName = null;
        if (joined)
        {
            dnsName = DomainNames.ChooseDnsName(
                GetComputerDnsDomain(),
                Environment.GetEnvironmentVariable("USERDNSDOMAIN"),
                Environment.UserDomainName,
                netBios,
                machine);
        }

        return new DomainEnvironmentInfo(joined, dnsName, machine, GetCurrentUserName());
    }

    public async Task<LogonResult> LogonAsync(LogonRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await LogonCoreAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = LdapErrorMapper.Map(ex);
            return LogonResult.Fail(failure.Error, failure.Message);
        }
    }

    private async Task<LogonResult> LogonCoreAsync(LogonRequest request, CancellationToken ct)
    {
        var environment = DetectEnvironment();
        var explicitMode = request.Mode == LogonMode.Explicit;

        ParsedAccountName? parsed = null;
        if (explicitMode)
        {
            if (string.IsNullOrWhiteSpace(request.UserName))
                return LogonResult.Fail(LogonError.InvalidCredentials, "Не указан логин. Введите его в виде ivanov, CORP\\ivanov или ivanov@corp.local.");
            if (string.IsNullOrEmpty(request.Password))
                return LogonResult.Fail(LogonError.InvalidCredentials, "Не указан пароль.");
            parsed = AccountNameParser.Parse(request.UserName);
        }

        var domain = DomainNames.ChooseLogonDomain(
            request.Domain,
            environment.IsDomainJoined ? environment.DomainName : null,
            parsed?.UpnSuffix);
        if (domain is null)
        {
            return LogonResult.Fail(
                LogonError.DomainNotSpecified,
                "Не указан домен. Введите DNS-имя домена, например corp.local: по короткому имени (CORP) контроллер домена не найти.");
        }

        AccountCredential? credential = null;
        NetworkCredential? network = null;
        SecurityIdentifier? currentUserSid = null;
        string accountName;

        if (parsed is not null)
        {
            // Для UPN домен в учётных данных пустой, а логин остаётся целым: суффикс UPN может отличаться от имени домена.
            var credentialDomain = parsed.IsUpn ? string.Empty : parsed.Domain ?? domain;
            var credentialUser = parsed.IsUpn ? $"{parsed.UserName}@{parsed.UpnSuffix}" : parsed.UserName;
            credential = new AccountCredential(credentialDomain, credentialUser, request.Password!);
            network = LdapConnectionFactory.BuildNetworkCredential(credential);
            accountName = DomainNames.FormatAccountName(parsed.Domain, domain, parsed.UserName);
        }
        else
        {
            (currentUserSid, accountName) = GetCurrentIdentity(environment);
        }

        var candidates = await ResolveControllersAsync(domain, request.DomainController, ct).ConfigureAwait(false);

        LogonFailure? lastFailure = null;
        foreach (var host in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await ProbeAsync(host, network, credential, currentUserSid, ct).ConfigureAwait(false);
            if (outcome.Failure is null)
            {
                var root = outcome.Root!.Value;
                var connection = new WindowsDomainConnection(
                    DomainNames.DnsDomainFromNamingContext(root.DefaultNamingContext, domain),
                    DomainNames.PickControllerName(host, root.DnsHostName),
                    accountName,
                    credential);
                return LogonResult.Ok(connection, outcome.IsPrivileged);
            }

            lastFailure = outcome.Failure;

            // Контроллер ответил, но учётные данные не подошли: другие контроллеры не пробуем,
            // иначе неверный пароль будет засчитан в счётчик блокировки на каждом из них.
            if (lastFailure.Value.Error is not (LogonError.DomainControllerUnreachable or LogonError.DnsFailure)) break;
        }

        var failure = lastFailure ?? new LogonFailure(LogonError.Unknown, "Не удалось подключиться к домену.");
        return LogonResult.Fail(failure.Error, failure.Message);
    }

    /// <summary>
    /// Контроллер из запроса либо найденные через DNS SRV (первые несколько по порядку) и, на крайний случай,
    /// само имя домена: в AD-интегрированном DNS оно указывает на контроллеры.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveControllersAsync(string domain, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return new[] { requested.Trim() };

        var hosts = new List<string>();
        try
        {
            var found = await Task.Run(() => DnsSrvResolver.FindDomainControllers(domain), CancellationToken.None)
                .WaitAsync(ct)
                .ConfigureAwait(false);
            hosts.AddRange(found.Take(MaxDiscoveredControllers).Select(r => r.Target));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SRV недоступен: пробуем имя домена.
        }

        if (!hosts.Contains(domain, StringComparer.OrdinalIgnoreCase)) hosts.Add(domain);
        return hosts;
    }

    private static async Task<ProbeOutcome> ProbeAsync(
        string host,
        NetworkCredential? network,
        AccountCredential? credential,
        SecurityIdentifier? currentUserSid,
        CancellationToken ct)
    {
        var portFailure = await CheckPortAsync(host, ct).ConfigureAwait(false);
        if (portFailure is not null) return ProbeOutcome.Fail(portFailure.Value);

        try
        {
            return await Task.Run(() => ProbeBlocking(host, network, credential, currentUserSid), CancellationToken.None)
                .WaitAsync(ProbeTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProbeOutcome.Fail(LdapErrorMapper.Map(ex, host, currentUser: credential is null));
        }
    }

    /// <summary>
    /// Быстрая проверка порта 389 до LDAP: она же разрешает имя через DNS и отличает «имя не найдено» от
    /// «контроллер не отвечает» (LDAP в обоих случаях отвечает одной и той же ошибкой 81).
    /// </summary>
    private static async Task<LogonFailure?> CheckPortAsync(string host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PortTimeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, LdapConnectionFactory.LdapPort, timeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LdapErrorMapper.Map(new TimeoutException(), host);
        }
        catch (SocketException ex)
        {
            return LdapErrorMapper.Map(ex, host);
        }
    }

    /// <summary>Блокирующая часть: Bind, rootDSE и проверка привилегий на одном подключении.</summary>
    private static ProbeOutcome ProbeBlocking(
        string host,
        NetworkCredential? network,
        AccountCredential? credential,
        SecurityIdentifier? currentUserSid)
    {
        using var ldap = LdapConnectionFactory.Create(host, network, ProbeTimeout);
        ldap.Bind();

        var rootRequest = new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "dnsHostName");
        var response = (SearchResponse)ldap.SendRequest(rootRequest);
        var attributes = response.Entries.Count > 0 ? new SearchEntryAttributes(response.Entries[0]) : null;

        var namingContext = attributes?.GetString("defaultNamingContext");
        if (string.IsNullOrWhiteSpace(namingContext))
        {
            return ProbeOutcome.Fail(new LogonFailure(
                LogonError.Unknown,
                $"Сервер «{host}» ответил, но не похож на контроллер домена Active Directory. Укажите контроллер домена вручную."));
        }

        var privileged = PrivilegeChecker.IsPrivileged(ldap, namingContext, credential, currentUserSid);
        return ProbeOutcome.Ok(new RootDse(namingContext, attributes!.GetString("dnsHostName")), privileged);
    }

    private static string? GetComputerDnsDomain()
    {
        try
        {
            unsafe
            {
                const int capacity = 256;
                char* buffer = stackalloc char[capacity];
                var size = (uint)capacity;
                return NativeMethods.GetComputerNameEx(NativeMethods.ComputerNameDnsDomain, buffer, ref size)
                    ? new string(buffer, 0, (int)size)
                    : null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string GetCurrentUserName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception)
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }

    private static (SecurityIdentifier? Sid, string Name) GetCurrentIdentity(DomainEnvironmentInfo environment)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return (identity.User, identity.Name);
        }
        catch (Exception)
        {
            return (null, environment.CurrentUser);
        }
    }

    private readonly record struct RootDse(string? DefaultNamingContext, string? DnsHostName);

    private readonly record struct ProbeOutcome(RootDse? Root, bool IsPrivileged, LogonFailure? Failure)
    {
        public static ProbeOutcome Ok(RootDse root, bool isPrivileged) => new(root, isPrivileged, null);

        public static ProbeOutcome Fail(LogonFailure failure) => new(null, false, failure);
    }
}
