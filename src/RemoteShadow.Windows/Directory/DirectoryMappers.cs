using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows;

/// <summary>Преобразование записей AD в модели Core. Работает с <see cref="ILdapAttributes"/>, поэтому проверяется без LDAP.</summary>
internal static class DirectoryMappers
{
    public static readonly string[] ComputerAttributes =
    {
        "name", "dNSHostName", "operatingSystem", "operatingSystemVersion", "description",
        "lastLogonTimestamp", "distinguishedName", "objectGUID", "whenChanged", "userAccountControl",
    };

    public static readonly string[] UserAttributes =
    {
        "objectGUID", "sAMAccountName", "distinguishedName", "displayName", "userPrincipalName", "department",
        "title", "telephoneNumber", "mail", "userAccountControl", "lockoutTime", "msDS-User-Account-Control-Computed",
        "pwdLastSet", "msDS-UserPasswordExpiryTimeComputed", "lastLogonTimestamp",
    };

    public static readonly string[] GroupAttributes = { "cn", "sAMAccountName", "distinguishedName", "description" };

    /// <summary>Компьютер из записи AD. null, если нет objectGUID: ключ записи — именно он.</summary>
    public static ComputerInfo? MapComputer(string distinguishedName, ILdapAttributes a)
    {
        var id = ReadGuid(a);
        if (id is null) return null;

        var name = a.GetString("name");
        var flags = LdapValueParser.ParseInt(a.GetString("userAccountControl"));

        return new ComputerInfo
        {
            Id = id.Value,
            Name = string.IsNullOrWhiteSpace(name) ? Dn.GetName(distinguishedName) : name,
            DnsName = NullIfEmpty(a.GetString("dNSHostName")),
            OperatingSystem = NullIfEmpty(a.GetString("operatingSystem")),
            OperatingSystemVersion = NullIfEmpty(a.GetString("operatingSystemVersion")),
            Description = NullIfEmpty(a.GetString("description")),
            DistinguishedName = distinguishedName,
            LastLogon = LdapValueParser.ParseFileTime(a.GetString("lastLogonTimestamp")),
            WhenChanged = LdapValueParser.ParseGeneralizedTime(a.GetString("whenChanged")),
            Enabled = flags is null || !UserAccountControl.IsDisabled(flags.Value),
        };
    }

    /// <summary>Пользователь из записи AD. null, если нет objectGUID или sAMAccountName.</summary>
    public static UserInfo? MapUser(string distinguishedName, ILdapAttributes a)
    {
        var id = ReadGuid(a);
        var sam = a.GetString("sAMAccountName");
        if (id is null || string.IsNullOrWhiteSpace(sam)) return null;

        var flags = LdapValueParser.ParseInt(a.GetString("userAccountControl")) ?? 0;
        var neverExpires = UserAccountControl.NeverExpires(flags);

        return new UserInfo
        {
            Id = id.Value,
            SamAccountName = sam,
            DistinguishedName = distinguishedName,
            DisplayName = NullIfEmpty(a.GetString("displayName")),
            UserPrincipalName = NullIfEmpty(a.GetString("userPrincipalName")),
            Department = NullIfEmpty(a.GetString("department")),
            Title = NullIfEmpty(a.GetString("title")),
            Phone = NullIfEmpty(a.GetString("telephoneNumber")),
            Mail = NullIfEmpty(a.GetString("mail")),
            Enabled = !UserAccountControl.IsDisabled(flags),
            LockedOut = IsLockedOut(a),
            PasswordNeverExpires = neverExpires,
            PasswordLastSet = LdapValueParser.ParseFileTime(a.GetString("pwdLastSet")),
            PasswordExpires = neverExpires ? null : LdapValueParser.ParseFileTime(a.GetString("msDS-UserPasswordExpiryTimeComputed")),
            LastLogon = LdapValueParser.ParseFileTime(a.GetString("lastLogonTimestamp")),
        };
    }

    /// <summary>Группа из записи AD. Имя — cn, иначе sAMAccountName, иначе первое значение DN.</summary>
    public static GroupInfo MapGroup(string distinguishedName, ILdapAttributes a)
    {
        var name = NullIfEmpty(a.GetString("cn")) ?? NullIfEmpty(a.GetString("sAMAccountName")) ?? Dn.GetName(distinguishedName);
        return new GroupInfo(name, distinguishedName, NullIfEmpty(a.GetString("description")));
    }

    /// <summary>
    /// Блокировка берётся из конструируемого атрибута msDS-User-Account-Control-Computed (бит 0x10): он учитывает срок
    /// блокировки. Если сервер его не отдал, остаётся lockoutTime &gt; 0: так видно, что блокировка была, но может быть уже снята по времени.
    /// </summary>
    internal static bool IsLockedOut(ILdapAttributes a)
    {
        var computed = LdapValueParser.ParseInt(a.GetString("msDS-User-Account-Control-Computed"));
        if (computed is not null) return UserAccountControl.IsLockedOut(computed.Value);

        return long.TryParse(a.GetString("lockoutTime"), out var lockoutTime) && lockoutTime > 0;
    }

    private static Guid? ReadGuid(ILdapAttributes a)
    {
        var bytes = a.GetBytes("objectGUID");
        return bytes is { Length: 16 } ? new Guid(bytes) : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Какие подразделения и контейнеры не показывать в дереве.</summary>
internal static class OuNodeFilter
{
    // Системные контейнеры корня домена: в обычном виде «Active Directory — пользователи и компьютеры» их нет.
    private static readonly HashSet<string> HiddenRootContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Program Data", "LostAndFound", "NTDS Quotas", "Keys", "TPM Devices",
        "Infrastructure", "ForeignSecurityPrincipals", "Managed Service Accounts",
    };

    /// <summary>
    /// true — узел скрыть. Скрываются объекты с showInAdvancedViewOnly=TRUE (как в консоли AD) и, независимо от атрибута,
    /// известные системные контейнеры непосредственно под корнем домена. Подразделения (OU) скрываются только по атрибуту.
    /// </summary>
    public static bool IsHidden(string rootDn, string parentDn, string name, bool isOrganizationalUnit, string? showInAdvancedViewOnly)
    {
        if (string.Equals(showInAdvancedViewOnly, "TRUE", StringComparison.OrdinalIgnoreCase)) return true;
        return !isOrganizationalUnit && Dn.AreEqual(parentDn, rootDn) && HiddenRootContainers.Contains(name);
    }
}
