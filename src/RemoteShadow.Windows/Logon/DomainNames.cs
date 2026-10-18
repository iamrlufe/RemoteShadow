using System.Net;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows;

/// <summary>Чистые функции для имён домена, контроллера и учётной записи.</summary>
internal static class DomainNames
{
    /// <summary>
    /// DNS-имя домена компьютера. NetGetJoinInformation отдаёт только NetBIOS-имя, поэтому сначала берём
    /// основной DNS-суффикс (GetComputerNameEx) и USERDNSDOMAIN, затем Environment.UserDomainName,
    /// и только в последнюю очередь NetBIOS-имя из NetGetJoinInformation.
    /// </summary>
    public static string? ChooseDnsName(
        string? computerDnsDomain,
        string? userDnsDomain,
        string? userDomainName,
        string? netBiosName,
        string machineName)
    {
        foreach (var candidate in new[] { computerDnsDomain, userDnsDomain })
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim().TrimEnd('.').ToLowerInvariant();
        }

        // Для локальной учётной записи UserDomainName равно имени компьютера: это не домен.
        if (!string.IsNullOrWhiteSpace(userDomainName)
            && !string.Equals(userDomainName, machineName, StringComparison.OrdinalIgnoreCase))
        {
            return userDomainName.Trim().ToLowerInvariant();
        }

        return string.IsNullOrWhiteSpace(netBiosName) ? null : netBiosName.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Домен для входа: из запроса, иначе домен этого компьютера, иначе суффикс UPN (ivanov@corp.local).
    /// null — домен неизвестен.
    /// </summary>
    public static string? ChooseLogonDomain(string? requested, string? environmentDomain, string? upnSuffix)
    {
        foreach (var candidate in new[] { requested, environmentDomain, upnSuffix })
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim().TrimEnd('.');
        }

        return null;
    }

    /// <summary>"corp.local" → "CORP".</summary>
    public static string FirstLabelUpper(string dnsDomain)
    {
        var label = dnsDomain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return (label ?? dnsDomain).ToUpperInvariant();
    }

    /// <summary>
    /// CORP\ivanov. NetBIOS-часть берётся из запроса (CORP\ivanov), иначе это первая метка DNS-имени домена
    /// в верхнем регистре: для corp.example.local она может не совпасть с настоящим NetBIOS-именем,
    /// поэтому используется только для показа, а не для проверки подлинности.
    /// </summary>
    public static string FormatAccountName(string? netBiosFromRequest, string dnsDomain, string userName)
    {
        var netBios = string.IsNullOrWhiteSpace(netBiosFromRequest) ? FirstLabelUpper(dnsDomain) : netBiosFromRequest.Trim();
        return $"{netBios}\\{userName}";
    }

    /// <summary>"DC=corp,DC=local" → "corp.local"; если контекста нет — <paramref name="fallback"/>.</summary>
    public static string DnsDomainFromNamingContext(string? namingContext, string fallback)
    {
        if (string.IsNullOrWhiteSpace(namingContext)) return fallback;
        var dns = Dn.ToDnsDomain(namingContext);
        return dns.Length == 0 ? fallback : dns;
    }

    /// <summary>
    /// Имя контроллера, которое запоминается в подключении. IP-адрес остаётся IP-адресом (на недоменном
    /// компьютере DNS-имя могло не разрешаться). Любое другое имя (в том числе имя самого домена, которое
    /// DNS раздаёт по кругу) заменяется на dnsHostName из rootDSE: так индекс компьютеров всегда ходит на один и тот же контроллер.
    /// </summary>
    public static string PickControllerName(string target, string? dnsHostName)
    {
        if (IPAddress.TryParse(target, out _)) return target;
        return string.IsNullOrWhiteSpace(dnsHostName) ? target : dnsHostName.Trim();
    }

    /// <summary>true для полного DNS-имени (есть точка, не IP-адрес).</summary>
    public static bool IsFqdn(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Contains('.') && !IPAddress.TryParse(host, out _);
}

/// <summary>Что показывать в строке состояния: «Kerberos» или «NTLM».</summary>
internal static class AuthenticationKinds
{
    public const string Kerberos = "Kerberos";
    public const string Ntlm = "NTLM";
    public const string Negotiate = "Negotiate";

    /// <summary>
    /// Эвристика: Negotiate выбирает Kerberos, когда есть SPN ldap/&lt;полное имя контроллера&gt; и учётная запись доменная.
    /// По IP-адресу или короткому имени SPN не находится, и Windows откатывается на NTLM.
    /// Это оценка, а не факт: настоящий пакет можно узнать только у SSPI после привязки.
    /// </summary>
    public static string Resolve(string? domainController, bool isDomainAccount)
    {
        if (string.IsNullOrWhiteSpace(domainController)) return Negotiate;
        return isDomainAccount && DomainNames.IsFqdn(domainController) ? Kerberos : Ntlm;
    }
}
