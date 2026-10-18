using System.Text;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

internal sealed class FakeAttributes : ILdapAttributes
{
    private readonly Dictionary<string, List<object>> _values = new(StringComparer.OrdinalIgnoreCase);

    public FakeAttributes Set(string name, params object[] values)
    {
        _values[name] = values.ToList();
        return this;
    }

    public string? GetString(string name) => GetStrings(name) is { Count: > 0 } list ? list[0] : null;

    public byte[]? GetBytes(string name) =>
        _values.TryGetValue(name, out var list) && list.Count > 0
            ? list[0] switch
            {
                byte[] bytes => bytes,
                string text => Encoding.UTF8.GetBytes(text),
                _ => null,
            }
            : null;

    public IReadOnlyList<string> GetStrings(string name) =>
        _values.TryGetValue(name, out var list) ? list.OfType<string>().ToList() : Array.Empty<string>();
}

public class DirectoryMappersTests
{
    private const string ComputerDn = "CN=PC-ACC-014,OU=Бухгалтерия,OU=Офис,DC=corp,DC=local";

    [Fact]
    public void MapComputer_FullRecord()
    {
        var id = Guid.NewGuid();
        var logon = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var attrs = new FakeAttributes()
            .Set("objectGUID", id.ToByteArray())
            .Set("name", "PC-ACC-014")
            .Set("dNSHostName", "pc-acc-014.corp.local")
            .Set("operatingSystem", "Windows 11 Pro")
            .Set("operatingSystemVersion", "10.0 (22631)")
            .Set("description", "Бухгалтерия, Петрова А.")
            .Set("lastLogonTimestamp", logon.ToFileTimeUtc().ToString())
            .Set("whenChanged", "20260302120000.0Z")
            .Set("userAccountControl", "4096");

        var computer = DirectoryMappers.MapComputer(ComputerDn, attrs);

        Assert.NotNull(computer);
        Assert.Equal(id, computer.Id);
        Assert.Equal("PC-ACC-014", computer.Name);
        Assert.Equal("pc-acc-014.corp.local", computer.DnsName);
        Assert.Equal("pc-acc-014.corp.local", computer.HostName);
        Assert.Equal("Windows 11 Pro", computer.OperatingSystem);
        Assert.Equal("10.0 (22631)", computer.OperatingSystemVersion);
        Assert.Equal("Бухгалтерия, Петрова А.", computer.Description);
        Assert.Equal(ComputerDn, computer.DistinguishedName);
        Assert.Equal(logon, computer.LastLogon);
        Assert.Equal(new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc), computer.WhenChanged);
        Assert.True(computer.Enabled);
        Assert.Equal("OU=Бухгалтерия,OU=Офис,DC=corp,DC=local", computer.ParentDn);
    }

    [Fact]
    public void MapComputer_DisabledByUacBit()
    {
        var attrs = new FakeAttributes()
            .Set("objectGUID", Guid.NewGuid().ToByteArray())
            .Set("name", "OLD-PC")
            .Set("userAccountControl", "4098");

        var computer = DirectoryMappers.MapComputer("CN=OLD-PC,DC=corp,DC=local", attrs);

        Assert.False(computer!.Enabled);
    }

    [Fact]
    public void MapComputer_MissingOptionalFields_AreNull()
    {
        var attrs = new FakeAttributes().Set("objectGUID", Guid.NewGuid().ToByteArray());

        var computer = DirectoryMappers.MapComputer("CN=PC1,OU=X,DC=corp,DC=local", attrs);

        Assert.NotNull(computer);
        Assert.Equal("PC1", computer.Name);
        Assert.Null(computer.DnsName);
        Assert.Equal("PC1", computer.HostName);
        Assert.Null(computer.LastLogon);
        Assert.Null(computer.WhenChanged);
        Assert.True(computer.Enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(17)]
    public void MapComputer_BadGuid_IsSkipped(int length)
    {
        var attrs = new FakeAttributes().Set("objectGUID", new byte[length]).Set("name", "PC1");

        Assert.Null(DirectoryMappers.MapComputer("CN=PC1,DC=corp,DC=local", attrs));
    }

    [Fact]
    public void MapComputer_NoGuid_IsSkipped()
    {
        Assert.Null(DirectoryMappers.MapComputer("CN=PC1,DC=corp,DC=local", new FakeAttributes().Set("name", "PC1")));
    }

    [Fact]
    public void MapComputer_LastLogonZero_IsNull()
    {
        var attrs = new FakeAttributes().Set("objectGUID", Guid.NewGuid().ToByteArray()).Set("lastLogonTimestamp", "0");

        Assert.Null(DirectoryMappers.MapComputer("CN=PC1,DC=corp,DC=local", attrs)!.LastLogon);
    }

    [Fact]
    public void MapUser_FullRecord()
    {
        var id = Guid.NewGuid();
        var pwdSet = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        var pwdExpires = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
        var logon = new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc);
        var attrs = new FakeAttributes()
            .Set("objectGUID", id.ToByteArray())
            .Set("sAMAccountName", "ivanov")
            .Set("displayName", "Иванов Иван")
            .Set("userPrincipalName", "ivanov@corp.local")
            .Set("department", "Бухгалтерия")
            .Set("title", "Главный бухгалтер")
            .Set("telephoneNumber", "+7 495 000-00-00")
            .Set("mail", "ivanov@corp.local")
            .Set("userAccountControl", "512")
            .Set("msDS-User-Account-Control-Computed", "0")
            .Set("pwdLastSet", pwdSet.ToFileTimeUtc().ToString())
            .Set("msDS-UserPasswordExpiryTimeComputed", pwdExpires.ToFileTimeUtc().ToString())
            .Set("lastLogonTimestamp", logon.ToFileTimeUtc().ToString());

        var user = DirectoryMappers.MapUser("CN=Иванов Иван,OU=Staff,DC=corp,DC=local", attrs);

        Assert.NotNull(user);
        Assert.Equal(id, user.Id);
        Assert.Equal("ivanov", user.SamAccountName);
        Assert.Equal("Иванов Иван", user.DisplayName);
        Assert.Equal("Иванов Иван", user.ShownName);
        Assert.Equal("ivanov@corp.local", user.UserPrincipalName);
        Assert.Equal("Бухгалтерия", user.Department);
        Assert.Equal("Главный бухгалтер", user.Title);
        Assert.Equal("+7 495 000-00-00", user.Phone);
        Assert.Equal("ivanov@corp.local", user.Mail);
        Assert.True(user.Enabled);
        Assert.False(user.LockedOut);
        Assert.False(user.PasswordNeverExpires);
        Assert.Equal(pwdSet, user.PasswordLastSet);
        Assert.Equal(pwdExpires, user.PasswordExpires);
        Assert.Equal(logon, user.LastLogon);
    }

    [Fact]
    public void MapUser_DisabledLockedNeverExpires()
    {
        var attrs = new FakeAttributes()
            .Set("objectGUID", Guid.NewGuid().ToByteArray())
            .Set("sAMAccountName", "svc")
            .Set("userAccountControl", (0x200 | 0x2 | 0x10000).ToString())
            .Set("msDS-User-Account-Control-Computed", "16")
            .Set("msDS-UserPasswordExpiryTimeComputed", "9223372036854775807");

        var user = DirectoryMappers.MapUser("CN=svc,DC=corp,DC=local", attrs)!;

        Assert.False(user.Enabled);
        Assert.True(user.LockedOut);
        Assert.True(user.PasswordNeverExpires);
        Assert.Null(user.PasswordExpires);
        Assert.Equal("svc", user.ShownName);
    }

    [Fact]
    public void MapUser_NeverExpires_IgnoresComputedExpiry()
    {
        var attrs = new FakeAttributes()
            .Set("objectGUID", Guid.NewGuid().ToByteArray())
            .Set("sAMAccountName", "svc")
            .Set("userAccountControl", "66048")
            .Set("msDS-UserPasswordExpiryTimeComputed", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc().ToString());

        Assert.Null(DirectoryMappers.MapUser("CN=svc,DC=corp,DC=local", attrs)!.PasswordExpires);
    }

    [Fact]
    public void MapUser_PwdLastSetZero_MeansMustChange()
    {
        var attrs = new FakeAttributes()
            .Set("objectGUID", Guid.NewGuid().ToByteArray())
            .Set("sAMAccountName", "new")
            .Set("pwdLastSet", "0");

        Assert.Null(DirectoryMappers.MapUser("CN=new,DC=corp,DC=local", attrs)!.PasswordLastSet);
    }

    [Fact]
    public void LockedOut_WithoutComputedAttribute_FallsBackToLockoutTime()
    {
        Assert.True(DirectoryMappers.IsLockedOut(new FakeAttributes().Set("lockoutTime", "133500000000000000")));
        Assert.False(DirectoryMappers.IsLockedOut(new FakeAttributes().Set("lockoutTime", "0")));
        Assert.False(DirectoryMappers.IsLockedOut(new FakeAttributes()));
    }

    [Fact]
    public void LockedOut_ComputedWinsOverLockoutTime()
    {
        var attrs = new FakeAttributes().Set("lockoutTime", "133500000000000000").Set("msDS-User-Account-Control-Computed", "0");

        Assert.False(DirectoryMappers.IsLockedOut(attrs));
    }

    [Fact]
    public void MapUser_WithoutGuidOrSam_IsSkipped()
    {
        Assert.Null(DirectoryMappers.MapUser("CN=x,DC=a", new FakeAttributes().Set("sAMAccountName", "x")));
        Assert.Null(DirectoryMappers.MapUser("CN=x,DC=a", new FakeAttributes().Set("objectGUID", Guid.NewGuid().ToByteArray())));
    }

    [Fact]
    public void MapGroup_UsesCnThenSamThenDn()
    {
        Assert.Equal(
            new RemoteShadow.Core.Models.GroupInfo("Sales", "CN=Sales,OU=G,DC=a", "Отдел продаж"),
            DirectoryMappers.MapGroup("CN=Sales,OU=G,DC=a", new FakeAttributes().Set("cn", "Sales").Set("description", "Отдел продаж")));

        Assert.Equal("sales-sam", DirectoryMappers.MapGroup("CN=Sales,DC=a", new FakeAttributes().Set("sAMAccountName", "sales-sam")).Name);
        Assert.Equal("Sales", DirectoryMappers.MapGroup("CN=Sales,DC=a", new FakeAttributes()).Name);
    }

    [Fact]
    public void RequestedAttributes_IncludeEverythingTheMappersRead()
    {
        string[] computerRead =
        {
            "name", "dNSHostName", "operatingSystem", "operatingSystemVersion", "description", "lastLogonTimestamp",
            "distinguishedName", "objectGUID", "whenChanged", "userAccountControl",
        };
        string[] userRead =
        {
            "objectGUID", "sAMAccountName", "distinguishedName", "displayName", "userPrincipalName", "department", "title",
            "telephoneNumber", "mail", "userAccountControl", "lockoutTime", "msDS-User-Account-Control-Computed",
            "pwdLastSet", "msDS-UserPasswordExpiryTimeComputed", "lastLogonTimestamp",
        };

        Assert.Empty(computerRead.Except(DirectoryMappers.ComputerAttributes));
        Assert.Empty(userRead.Except(DirectoryMappers.UserAttributes));
    }
}

public class OuNodeFilterTests
{
    private const string Root = "DC=corp,DC=local";

    [Theory]
    [InlineData("System")]
    [InlineData("Program Data")]
    [InlineData("ForeignSecurityPrincipals")]
    [InlineData("LostAndFound")]
    [InlineData("Managed Service Accounts")]
    [InlineData("NTDS Quotas")]
    [InlineData("system")]
    public void SystemContainersUnderRoot_AreHidden(string name)
    {
        Assert.True(OuNodeFilter.IsHidden(Root, Root, name, isOrganizationalUnit: false, showInAdvancedViewOnly: null));
    }

    [Theory]
    [InlineData("Computers")]
    [InlineData("Users")]
    public void DefaultVisibleContainers_AreShown(string name)
    {
        Assert.False(OuNodeFilter.IsHidden(Root, Root, name, isOrganizationalUnit: false, showInAdvancedViewOnly: null));
        Assert.False(OuNodeFilter.IsHidden(Root, Root, name, isOrganizationalUnit: false, showInAdvancedViewOnly: "FALSE"));
    }

    [Fact]
    public void OuNamedSystem_IsShown()
    {
        Assert.False(OuNodeFilter.IsHidden(Root, Root, "System", isOrganizationalUnit: true, showInAdvancedViewOnly: null));
    }

    [Fact]
    public void SameNameBelowRoot_IsShown()
    {
        Assert.False(OuNodeFilter.IsHidden(Root, "OU=Офис,DC=corp,DC=local", "System", isOrganizationalUnit: false, showInAdvancedViewOnly: null));
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData(null, false)]
    public void AdvancedViewOnlyAttribute_HidesNode(string? attribute, bool hidden)
    {
        Assert.Equal(hidden, OuNodeFilter.IsHidden(Root, "OU=Офис,DC=corp,DC=local", "Склад", isOrganizationalUnit: true, attribute));
    }

    [Fact]
    public void RootComparisonIgnoresCaseAndSpaces()
    {
        Assert.True(OuNodeFilter.IsHidden("dc=corp, dc=local", "DC=Corp,DC=Local", "System", false, null));
    }
}
