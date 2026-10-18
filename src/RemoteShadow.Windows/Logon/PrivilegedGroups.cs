using System.DirectoryServices.Protocols;
using System.Security.Principal;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows;

/// <summary>
/// Привилегированные группы Active Directory. Классификация по SID/RID, а не по имени: имена групп локализуются
/// («Администраторы домена») и переименовываются.
/// </summary>
internal static class PrivilegedGroups
{
    /// <summary>RID групп домена в SID вида S-1-5-21-a-b-c-RID: Domain Admins, Schema Admins, Enterprise Admins.</summary>
    private static readonly IReadOnlyDictionary<int, string> DomainGroups = new Dictionary<int, string>
    {
        [512] = "Domain Admins",
        [518] = "Schema Admins",
        [519] = "Enterprise Admins",
    };

    /// <summary>RID встроенных групп в SID вида S-1-5-32-RID: Administrators, Account/Server/Backup Operators.</summary>
    private static readonly IReadOnlyDictionary<int, string> BuiltinGroups = new Dictionary<int, string>
    {
        [544] = "Administrators",
        [548] = "Account Operators",
        [549] = "Server Operators",
        [551] = "Backup Operators",
    };

    private const string BuiltinPrefix = "S-1-5-32-";
    private const string DomainPrefix = "S-1-5-21-";

    /// <summary>
    /// Имя привилегированной группы по SID в строковом виде или null. Для групп домена SID должен быть
    /// S-1-5-21-a-b-c-RID (ровно три подавторитета домена): вложенные SID вроде S-1-5-21-a-b-c-512-1 не считаются.
    /// </summary>
    public static string? Classify(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return null;

        if (sid.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
        {
            var tail = sid[BuiltinPrefix.Length..];
            return int.TryParse(tail, out var rid) && BuiltinGroups.TryGetValue(rid, out var name) ? name : null;
        }

        if (sid.StartsWith(DomainPrefix, StringComparison.Ordinal))
        {
            var parts = sid.Split('-');
            if (parts.Length != 8) return null; // S, 1, 5, 21, a, b, c, RID
            return int.TryParse(parts[7], out var rid) && DomainGroups.TryGetValue(rid, out var name) ? name : null;
        }

        return null;
    }

    /// <summary>Имена привилегированных групп среди SID (без повторов).</summary>
    public static IReadOnlyList<string> FindPrivileged(IEnumerable<string> sids) =>
        sids.Select(Classify).Where(n => n is not null).Select(n => n!).Distinct().ToList();

    public static bool IsPrivileged(IEnumerable<string> sids) => FindPrivileged(sids).Count > 0;
}

/// <summary>
/// Проверяет по LDAP, входит ли учётная запись (с учётом вложенности) в привилегированные группы.
/// Берёт tokenGroups пользователя: контроллер сам раскрывает вложенные группы и добавляет основную группу и встроенные.
/// </summary>
internal static class PrivilegeChecker
{
    /// <summary>
    /// Любая ошибка проверки даёт false: проверка привилегий не должна ломать вход (так задано в задании).
    /// Для текущего пользователя передают его SID (WindowsIdentity), для явных данных — исходные учётные данные.
    /// </summary>
    public static bool IsPrivileged(
        LdapConnection ldap,
        string defaultNamingContext,
        AccountCredential? credential,
        SecurityIdentifier? currentUserSid)
    {
        try
        {
            var baseDn = currentUserSid is not null
                ? $"<SID={currentUserSid.Value}>"
                : FindUserDn(ldap, defaultNamingContext, credential);
            if (baseDn is null) return false;

            var request = new SearchRequest(baseDn, "(objectClass=*)", SearchScope.Base, "tokenGroups");
            var response = (SearchResponse)ldap.SendRequest(request);
            if (response.Entries.Count == 0) return false;

            var attribute = response.Entries[0].Attributes["tokenGroups"];
            if (attribute is null) return false;

            var sids = new List<string>();
            foreach (var value in attribute.GetValues(typeof(byte[])))
            {
                if (value is byte[] bytes && bytes.Length >= 8)
                    sids.Add(new SecurityIdentifier(bytes, 0).Value);
            }

            return PrivilegedGroups.IsPrivileged(sids);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? FindUserDn(LdapConnection ldap, string defaultNamingContext, AccountCredential? credential)
    {
        if (credential is null) return null;

        var parsed = AccountNameParser.Parse(credential.UserName);
        var filter = parsed.IsUpn
            ? LdapFilters.UserByUpn(credential.UserName)
            : LdapFilters.UserBySam(parsed.UserName);

        var request = new SearchRequest(defaultNamingContext, filter, SearchScope.Subtree, "distinguishedName")
        {
            SizeLimit = 2,
        };
        var response = (SearchResponse)ldap.SendRequest(request);
        return response.Entries.Count == 1 ? response.Entries[0].DistinguishedName : null;
    }
}
