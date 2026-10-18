using System.Runtime.InteropServices;
using System.Security.Principal;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class PrivilegedGroupsTests
{
    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-512", "Domain Admins")]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-518", "Schema Admins")]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-519", "Enterprise Admins")]
    [InlineData("S-1-5-32-544", "Administrators")]
    [InlineData("S-1-5-32-548", "Account Operators")]
    [InlineData("S-1-5-32-549", "Server Operators")]
    [InlineData("S-1-5-32-551", "Backup Operators")]
    public void Privileged(string sid, string expected)
    {
        Assert.Equal(expected, PrivilegedGroups.Classify(sid));
    }

    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-513")] // Domain Users
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-515")] // Domain Computers
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1105")] // обычная группа
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-512-1")] // лишний подавторитет
    [InlineData("S-1-5-21-1111111111-2222222222-512")] // не хватает подавторитета домена
    [InlineData("S-1-5-32-545")] // Users
    [InlineData("S-1-5-32-546")] // Guests
    [InlineData("S-1-5-32-550")] // Print Operators: не из списка
    [InlineData("S-1-5-32-abc")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-1-0")]
    [InlineData("")]
    [InlineData(null)]
    public void NotPrivileged(string? sid)
    {
        Assert.Null(PrivilegedGroups.Classify(sid));
    }

    [Fact]
    public void FindPrivileged_ReturnsDistinctNames()
    {
        var found = PrivilegedGroups.FindPrivileged(new[]
        {
            "S-1-5-21-1-2-3-513",
            "S-1-5-21-1-2-3-512",
            "S-1-5-21-9-8-7-512", // Domain Admins другого домена
            "S-1-5-32-544",
            "S-1-5-32-545",
        });

        Assert.Equal(new[] { "Domain Admins", "Administrators" }, found);
    }

    [Fact]
    public void IsPrivileged_FalseForRegularUser()
    {
        Assert.False(PrivilegedGroups.IsPrivileged(new[] { "S-1-5-21-1-2-3-513", "S-1-5-32-545", "S-1-5-21-1-2-3-1105" }));
        Assert.False(PrivilegedGroups.IsPrivileged(Array.Empty<string>()));
    }

    [Fact]
    public void IsPrivileged_TrueForOperators()
    {
        Assert.True(PrivilegedGroups.IsPrivileged(new[] { "S-1-5-21-1-2-3-513", "S-1-5-32-551" }));
    }

    [Fact]
    public void WorksWithBinarySidFromTokenGroups()
    {
        var sid = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-512");
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);

        Assert.Equal("Domain Admins", PrivilegedGroups.Classify(new SecurityIdentifier(bytes, 0).Value));
    }
}

public class SrvRecordTests
{
    [Fact]
    public void Order_ByPriorityThenWeightDescending()
    {
        var ordered = SrvRecord.Order(new[]
        {
            new SrvRecord("dc3.corp.local", 20, 100, 389),
            new SrvRecord("dc1.corp.local", 0, 50, 389),
            new SrvRecord("dc2.corp.local", 0, 100, 389),
            new SrvRecord("dc4.corp.local", 10, 0, 389),
        });

        Assert.Equal(new[] { "dc2.corp.local", "dc1.corp.local", "dc4.corp.local", "dc3.corp.local" }, ordered.Select(r => r.Target));
    }

    [Fact]
    public void Order_TrimsTrailingDotAndDropsEmptyOrDot()
    {
        var ordered = SrvRecord.Order(new[]
        {
            new SrvRecord("dc1.corp.local.", 0, 100, 389),
            new SrvRecord(".", 0, 100, 389),
            new SrvRecord("  ", 0, 100, 389),
        });

        Assert.Equal("dc1.corp.local", Assert.Single(ordered).Target);
    }

    [Fact]
    public void Order_RemovesDuplicatesIgnoringCase()
    {
        var ordered = SrvRecord.Order(new[]
        {
            new SrvRecord("DC1.corp.local", 0, 100, 389),
            new SrvRecord("dc1.corp.local.", 5, 100, 389),
        });

        Assert.Single(ordered);
        Assert.Equal((ushort)0, ordered[0].Priority);
    }

    [Fact]
    public void Order_SameKeys_IsStableByName()
    {
        var ordered = SrvRecord.Order(new[]
        {
            new SrvRecord("b.corp.local", 0, 100, 389),
            new SrvRecord("a.corp.local", 0, 100, 389),
        });

        Assert.Equal(new[] { "a.corp.local", "b.corp.local" }, ordered.Select(r => r.Target));
    }

    [Fact]
    public void Order_Empty()
    {
        Assert.Empty(SrvRecord.Order(Array.Empty<SrvRecord>()));
    }

    [Fact]
    public void ReadSrvRecords_ParsesNativeRecordListAndSkipsOtherTypes()
    {
        var allocations = new List<IntPtr>();
        try
        {
            // Цепочка: SRV(dc1) -> A -> SRV(dc2)
            var third = BuildRecord(allocations, "dc2.corp.local", 10, 20, 389, type: 0x21, next: IntPtr.Zero);
            var second = BuildRecord(allocations, "ignored", 1, 2, 3, type: 0x01, next: third);
            var first = BuildRecord(allocations, "dc1.corp.local", 0, 100, 389, type: 0x21, next: second);

            var records = DnsSrvResolver.ReadSrvRecords(first);

            Assert.Equal(2, records.Count);
            Assert.Equal(new SrvRecord("dc1.corp.local", 0, 100, 389), records[0]);
            Assert.Equal(new SrvRecord("dc2.corp.local", 10, 20, 389), records[1]);
        }
        finally
        {
            foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void ReadSrvRecords_EmptyList()
    {
        Assert.Empty(DnsSrvResolver.ReadSrvRecords(IntPtr.Zero));
    }

    /// <summary>DNS_RECORD: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, затем DNS_SRV_DATAW.</summary>
    private static IntPtr BuildRecord(List<IntPtr> allocations, string target, ushort priority, ushort weight, ushort port, ushort type, IntPtr next)
    {
        var dataOffset = IntPtr.Size * 2 + 16;
        var size = dataOffset + IntPtr.Size + 8;
        var record = Marshal.AllocHGlobal(size);
        allocations.Add(record);
        for (var i = 0; i < size; i++) Marshal.WriteByte(record, i, 0);

        var name = Marshal.StringToHGlobalUni(target);
        allocations.Add(name);

        Marshal.WriteIntPtr(record, 0, next);
        Marshal.WriteInt16(record, IntPtr.Size * 2, unchecked((short)type));
        var data = IntPtr.Add(record, dataOffset);
        Marshal.WriteIntPtr(data, 0, name);
        Marshal.WriteInt16(data, IntPtr.Size, unchecked((short)priority));
        Marshal.WriteInt16(data, IntPtr.Size + 2, unchecked((short)weight));
        Marshal.WriteInt16(data, IntPtr.Size + 4, unchecked((short)port));
        return record;
    }
}

public class DomainNamesTests
{
    [Fact]
    public void ChooseDnsName_PrefersComputerDnsDomain()
    {
        Assert.Equal("corp.local", DomainNames.ChooseDnsName("Corp.Local.", "OTHER.LOCAL", "CORP", "CORP", "PC1"));
    }

    [Fact]
    public void ChooseDnsName_FallsBackToUserDnsDomain()
    {
        Assert.Equal("corp.local", DomainNames.ChooseDnsName(null, "CORP.LOCAL", "CORP", "CORP", "PC1"));
        Assert.Equal("corp.local", DomainNames.ChooseDnsName("  ", "CORP.LOCAL", null, null, "PC1"));
    }

    [Fact]
    public void ChooseDnsName_UserDomainNameIgnoredWhenItIsMachineName()
    {
        Assert.Equal("corp", DomainNames.ChooseDnsName(null, null, "PC1", "CORP", "pc1"));
        Assert.Equal("corp", DomainNames.ChooseDnsName(null, null, "CORP", "OTHER", "PC1"));
    }

    [Fact]
    public void ChooseDnsName_LastResortIsNetBios()
    {
        Assert.Equal("corp", DomainNames.ChooseDnsName(null, null, null, "CORP", "PC1"));
        Assert.Null(DomainNames.ChooseDnsName(null, null, null, null, "PC1"));
    }

    [Fact]
    public void ChooseLogonDomain_RequestThenEnvironmentThenUpn()
    {
        Assert.Equal("a.local", DomainNames.ChooseLogonDomain(" a.local ", "b.local", "c.local"));
        Assert.Equal("b.local", DomainNames.ChooseLogonDomain(null, "b.local", "c.local"));
        Assert.Equal("c.local", DomainNames.ChooseLogonDomain("  ", null, "c.local"));
        Assert.Null(DomainNames.ChooseLogonDomain(null, null, null));
        Assert.Equal("a.local", DomainNames.ChooseLogonDomain("a.local.", null, null));
    }

    [Theory]
    [InlineData("corp.local", "CORP")]
    [InlineData("corp.example.local", "CORP")]
    [InlineData("Corp", "CORP")]
    public void FirstLabelUpper(string domain, string expected)
    {
        Assert.Equal(expected, DomainNames.FirstLabelUpper(domain));
    }

    [Fact]
    public void FormatAccountName_UsesRequestedNetBiosOrFirstLabel()
    {
        Assert.Equal("EXAMPLE\\ivanov", DomainNames.FormatAccountName("EXAMPLE", "corp.example.local", "ivanov"));
        Assert.Equal("CORP\\ivanov", DomainNames.FormatAccountName(null, "corp.local", "ivanov"));
        Assert.Equal("CORP\\ivanov", DomainNames.FormatAccountName("  ", "corp.local", "ivanov"));
    }

    [Theory]
    [InlineData("DC=corp,DC=local", "x", "corp.local")]
    [InlineData("DC=corp,DC=example,DC=local", "x", "corp.example.local")]
    [InlineData(null, "fallback.local", "fallback.local")]
    [InlineData("", "fallback.local", "fallback.local")]
    [InlineData("OU=NoDc", "fallback.local", "fallback.local")]
    public void DnsDomainFromNamingContext(string? context, string fallback, string expected)
    {
        Assert.Equal(expected, DomainNames.DnsDomainFromNamingContext(context, fallback));
    }

    [Fact]
    public void PickControllerName_KeepsIpAddress()
    {
        Assert.Equal("10.0.0.5", DomainNames.PickControllerName("10.0.0.5", "dc01.corp.local"));
    }

    [Fact]
    public void PickControllerName_UsesDnsHostNameForNamesAndDomain()
    {
        Assert.Equal("dc01.corp.local", DomainNames.PickControllerName("corp.local", "dc01.corp.local"));
        Assert.Equal("dc01.corp.local", DomainNames.PickControllerName("dc01", "dc01.corp.local"));
        Assert.Equal("corp.local", DomainNames.PickControllerName("corp.local", null));
        Assert.Equal("corp.local", DomainNames.PickControllerName("corp.local", " "));
    }

    [Theory]
    [InlineData("dc01.corp.local", true)]
    [InlineData("dc01", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFqdn(string? host, bool expected)
    {
        Assert.Equal(expected, DomainNames.IsFqdn(host));
    }

    [Theory]
    [InlineData("dc01.corp.local", true, "Kerberos")]
    [InlineData("dc01.corp.local", false, "NTLM")]
    [InlineData("10.0.0.5", true, "NTLM")]
    [InlineData("dc01", true, "NTLM")]
    [InlineData(null, true, "Negotiate")]
    [InlineData(" ", true, "Negotiate")]
    public void AuthenticationKind(string? controller, bool domainAccount, string expected)
    {
        Assert.Equal(expected, AuthenticationKinds.Resolve(controller, domainAccount));
    }
}
