using System.Security.Cryptography.X509Certificates;
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

public class RdpCertificateProbeTests
{
    [Fact]
    public void Server_choosing_tls_or_credssp_allows_the_handshake()
    {
        byte[] Response(byte protocol) => new byte[] { 3, 0, 0, 19, 14, 0xD0, 0, 0, 0, 0, 0, 2, 0, 8, 0, protocol, 0, 0, 0 };

        Assert.True(RdpCertificateProbe.ServerSelectedTls(Response(1)));
        Assert.True(RdpCertificateProbe.ServerSelectedTls(Response(2)));
        Assert.False(RdpCertificateProbe.ServerSelectedTls(Response(0))); // стандартная защита RDP, TLS нет
        Assert.False(RdpCertificateProbe.ServerSelectedTls(new byte[] { 3, 0, 0, 19, 14, 0xD0, 0, 0, 0, 0, 0, 3, 0, 8, 0, 5, 0, 0, 0 })); // отказ
        Assert.False(RdpCertificateProbe.ServerSelectedTls(new byte[3]));
    }

    [Fact]
    public void Connection_request_is_a_well_formed_tpkt()
    {
        Assert.Equal(RdpCertificateProbe.ConnectionRequest.Length, RdpCertificateProbe.ConnectionRequest[3]);
        Assert.Equal(0x03, RdpCertificateProbe.ConnectionRequest[0]);
    }

    [Fact]
    public void Certificate_name_prefers_a_host_name_and_skips_ip_addresses()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        X509Certificate2 Make(string subject)
        {
            var request = new CertificateRequest(subject, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        Assert.Equal("pc-acc-014.corp.local", RdpCertificateProbe.NameFromCertificate(Make("CN=pc-acc-014.corp.local")));
        Assert.Null(RdpCertificateProbe.NameFromCertificate(Make("CN=10.0.0.196")));
    }
}
