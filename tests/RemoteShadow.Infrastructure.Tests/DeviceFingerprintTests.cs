using System.Text;
using RemoteShadow.Infrastructure;

namespace RemoteShadow.Infrastructure.Tests;

public class DeviceFingerprintTests
{
    private static byte[] NbstatResponse((string Name, byte Suffix, bool Group)[] names, byte[] mac)
    {
        var data = new List<byte>(new byte[56]);
        data.Add((byte)names.Length);
        foreach (var (name, suffix, group) in names)
        {
            data.AddRange(Encoding.ASCII.GetBytes(name.PadRight(15)));
            data.Add(suffix);
            data.Add((byte)(group ? 0x80 : 0x04));
            data.Add(0);
        }

        data.AddRange(mac);
        data.AddRange(new byte[40]);
        return data.ToArray();
    }

    [Fact]
    public void NetBios_response_gives_name_workgroup_and_mac()
    {
        var packet = NbstatResponse(new[] { ("PC-ACC-014", (byte)0, false), ("WORKGROUP", (byte)0, true), ("PC-ACC-014", (byte)0x20, false) },
            new byte[] { 0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E });

        var r = NetBiosProbe.Parse(packet);

        Assert.NotNull(r);
        Assert.Equal("PC-ACC-014", r!.Name);
        Assert.Equal("WORKGROUP", r.Workgroup);
        Assert.Equal("00:1A:2B:3C:4D:5E", r.Mac);
    }

    [Fact]
    public void NetBios_garbage_is_ignored()
    {
        Assert.Null(NetBiosProbe.Parse(new byte[10]));
        Assert.Null(NetBiosProbe.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Oui_lookup_accepts_common_mac_formats()
    {
        var db = new OuiDatabase(() => new Dictionary<string, string> { ["001A2B"] = "Acme" });

        Assert.Equal("Acme", db.Lookup("00:1a:2b:3c:4d:5e"));
        Assert.Equal("Acme", db.Lookup("00-1A-2B-3C-4D-5E"));
        Assert.Equal("Acme", db.Lookup("001a.2b3c.4d5e"));
        Assert.Null(db.Lookup("AA:BB:CC:00:00:00"));
        Assert.Null(db.Lookup("12"));
    }

    [Fact]
    public void Embedded_oui_database_knows_a_real_vendor()
    {
        // 00:50:56 — VMware.
        Assert.Contains("VMware", new OuiDatabase().Lookup("00:50:56:AA:BB:CC"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(new[] { 9100 }, null, null, "Принтер")]
    [InlineData(new[] { 445, 3389 }, "Dell Inc.", null, "Windows")]
    [InlineData(new[] { 80 }, "Hikvision Digital", null, "Камера")]
    [InlineData(new[] { 80 }, "MikroTik", null, "Сетевое оборудование")]
    [InlineData(new[] { 22 }, null, "SSH-2.0-OpenSSH_9.6", "Linux/Unix")]
    [InlineData(new int[0], null, null, null)]
    public void Kind_is_guessed_from_ports_vendor_and_banner(int[] ports, string? vendor, string? banner, string? expected)
        => Assert.Equal(expected, DeviceFingerprinter.ClassifyKind(ports.ToHashSet(), vendor, banner));
}
