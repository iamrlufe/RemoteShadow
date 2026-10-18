using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows;

/// <summary>
/// Построение LDAP-фильтров. Любой текст, пришедший от пользователя, проходит через <see cref="LdapFilter.Escape"/>:
/// символы * ( ) \ и NUL не могут изменить смысл фильтра.
/// </summary>
internal static class LdapFilters
{
    /// <summary>LDAP_MATCHING_RULE_BIT_AND.</summary>
    public const string BitAnd = "1.2.840.113556.1.4.803";

    /// <summary>Подразделения и контейнеры (дерево OU).</summary>
    public const string OuAndContainers = "(|(objectClass=organizationalUnit)(objectClass=container))";

    /// <summary>Подразделения и контейнеры, кроме скрытых в обычном виде (showInAdvancedViewOnly=TRUE).</summary>
    public const string VisibleOuAndContainers = "(&" + OuAndContainers + "(!(showInAdvancedViewOnly=TRUE)))";

    /// <summary>
    /// Компьютеры: по умолчанию без отключённых (бит 0x2 в userAccountControl); при ChangedSince — только
    /// изменённые с этого момента (whenChanged).
    /// </summary>
    public static string Computers(ComputerQuery query)
    {
        var clauses = new List<string> { "(objectCategory=computer)" };
        if (!query.IncludeDisabled) clauses.Add($"(!(userAccountControl:{BitAnd}:=2))");
        if (query.ChangedSince is { } since) clauses.Add($"(whenChanged>={LdapFilter.GeneralizedTime(since)})");
        return And(clauses);
    }

    /// <summary>Пользователи по ФИО, логину, отделу и телефону. <paramref name="text"/> экранируется.</summary>
    public static string UserSearch(string text)
    {
        var t = LdapFilter.Escape(text);
        return "(&(objectCategory=person)(objectClass=user)"
               + $"(|(anr={t})(sAMAccountName={t}*)(displayName=*{t}*)(department=*{t}*)(telephoneNumber=*{t}*)))";
    }

    /// <summary>Пользователь с точным sAMAccountName.</summary>
    public static string UserBySam(string samAccountName) =>
        $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={LdapFilter.Escape(samAccountName)}))";

    /// <summary>Пользователь с точным userPrincipalName.</summary>
    public static string UserByUpn(string userPrincipalName) =>
        $"(&(objectCategory=person)(objectClass=user)(userPrincipalName={LdapFilter.Escape(userPrincipalName)}))";

    /// <summary>Группы по имени или логину группы.</summary>
    public static string GroupSearch(string text)
    {
        var t = LdapFilter.Escape(text);
        return $"(&(objectCategory=group)(|(cn=*{t}*)(sAMAccountName=*{t}*)))";
    }

    private static string And(IReadOnlyList<string> clauses) =>
        clauses.Count == 1 ? clauses[0] : "(&" + string.Concat(clauses) + ")";
}
