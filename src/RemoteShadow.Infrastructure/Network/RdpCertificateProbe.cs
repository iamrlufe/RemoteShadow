using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Имя Windows-компьютера из TLS-сертификата службы RDP (порт 3389). Без пароля: отправляется только X.224-запрос
/// соединения с согласованием TLS, затем обычное рукопожатие TLS. Сертификат RDP самоподписанный, поэтому проверка
/// цепочки отключена: нам нужно только имя в нём, доверять ему мы не собираемся.
/// </summary>
internal static class RdpCertificateProbe
{
    /// <summary>TPKT + X.224 Connection Request + RDP_NEG_REQ (запрошены протоколы TLS и CredSSP).</summary>
    internal static readonly byte[] ConnectionRequest =
    {
        0x03, 0x00, 0x00, 0x13,
        0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00,
    };

    public static async Task<string?> ReadNameAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout + timeout + timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(ip), 3389, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(ConnectionRequest, cts.Token).ConfigureAwait(false);

            var response = new byte[64];
            var read = await stream.ReadAsync(response, cts.Token).ConfigureAwait(false);
            if (!ServerSelectedTls(response.AsSpan(0, read))) return null;

            string? name = null;
            using var ssl = new SslStream(stream, false, (_, certificate, _, _) =>
            {
                if (certificate is not null) name = NameFromCertificate(new X509Certificate2(certificate));
                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ip }, cts.Token).ConfigureAwait(false);
            return name;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>true — сервер ответил Connection Confirm с RDP_NEG_RSP и выбрал TLS (1), CredSSP (2) или CredSSP+EarlyAuth (8).</summary>
    internal static bool ServerSelectedTls(ReadOnlySpan<byte> r)
    {
        // TPKT(4) + X.224 CC(7) = 11 байт, дальше RDP_NEG_RSP: тип 2, флаги, длина 8, выбранный протокол (4 байта).
        if (r.Length < 19 || r[0] != 0x03 || r[5] != 0xD0 || r[11] != 0x02) return false;
        var selected = r[15] | (r[16] << 8) | (r[17] << 16) | (r[18] << 24);
        return selected is 1 or 2 or 8;
    }

    /// <summary>Имя компьютера из сертификата: DNS-имя из SAN, иначе CN. Адреса и пустые значения не подходят.</summary>
    internal static string? NameFromCertificate(X509Certificate2 certificate)
    {
        var candidate = certificate.GetNameInfo(X509NameType.DnsName, false);
        if (string.IsNullOrWhiteSpace(candidate)) candidate = certificate.GetNameInfo(X509NameType.SimpleName, false);
        candidate = candidate?.Trim();
        if (string.IsNullOrEmpty(candidate) || IPAddress.TryParse(candidate, out _)) return null;
        return candidate;
    }
}
