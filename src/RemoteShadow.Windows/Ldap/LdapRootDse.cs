using System.DirectoryServices.Protocols;
using RemoteShadow.Core;

namespace RemoteShadow.Windows;

/// <summary>Чтение rootDSE: корень каталога домена.</summary>
internal static class LdapRootDse
{
    /// <summary>defaultNamingContext, например DC=corp,DC=local. Блокирующий вызов.</summary>
    public static string ReadDefaultNamingContext(LdapConnection ldap)
    {
        var request = new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, "defaultNamingContext");
        var response = (SearchResponse)ldap.SendRequest(request);
        var root = response.Entries.Count > 0
            ? new SearchEntryAttributes(response.Entries[0]).GetString("defaultNamingContext")
            : null;

        return string.IsNullOrWhiteSpace(root)
            ? throw new OperationFailedException(
                "Контроллер домена не вернул корень каталога (defaultNamingContext). Убедитесь, что это контроллер домена Active Directory.",
                OperationFailure.Other)
            : root;
    }
}
