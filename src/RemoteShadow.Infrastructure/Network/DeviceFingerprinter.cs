using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Опознаёт устройство без учётных данных: NetBIOS-имя и MAC (UDP 137), MAC из ARP, производитель по OUI,
/// баннеры SSH и HTTP, тип по открытым портам и производителю. Все источники необязательны: сбой одного не мешает остальным.
/// </summary>
public sealed class DeviceFingerprinter : IDeviceFingerprinter
{
    private static readonly int[] KindPorts = { 22, 80, 135, 443, 445, 515, 554, 631, 3389, 9100 };

    private static readonly string[] PrinterVendors = { "Hewlett", "Canon", "Epson", "Brother", "Xerox", "Kyocera", "Ricoh", "Lexmark", "Konica", "Sharp", "Pantum", "OKI" };
    private static readonly string[] NetworkVendors = { "Cisco", "MikroTik", "Ubiquiti", "TP-Link", "Zyxel", "Juniper", "Huawei", "D-Link", "Netgear", "Aruba", "Fortinet", "Eltex", "Tenda" };
    private static readonly string[] CameraVendors = { "Hikvision", "Dahua", "Axis", "Uniview", "Reolink" };

    private readonly IHostProbe _tcp;
    private readonly IOuiDatabase _oui;
    private readonly IArpResolver? _arp;

    public DeviceFingerprinter(IHostProbe tcp, IOuiDatabase oui, IArpResolver? arp = null)
    {
        _tcp = tcp ?? throw new ArgumentNullException(nameof(tcp));
        _oui = oui ?? throw new ArgumentNullException(nameof(oui));
        _arp = arp;
    }

    public async Task<DeviceFingerprint> ProbeAsync(string ipAddress, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var netbiosTask = SafeAsync(() => NetBiosProbe.QueryAsync(ipAddress, timeout, ct));
        var portsTask = SafeAsync(() => OpenPortsAsync(ipAddress, timeout, ct));
        await Task.WhenAll(netbiosTask, portsTask).ConfigureAwait(false);
        var netbios = netbiosTask.Result;
        var ports = portsTask.Result ?? new HashSet<int>();

        var mac = _arp?.TryGetMac(ipAddress) ?? netbios?.Mac;
        var vendor = mac is null ? null : _oui.Lookup(mac);

        string? banner = null;
        if (ports.Contains(22)) banner = await SafeAsync(() => ReadSshBannerAsync(ipAddress, timeout, ct)).ConfigureAwait(false);
        if (banner is null && ports.Contains(80)) banner = await SafeAsync(() => ReadHttpBannerAsync(ipAddress, timeout, ct)).ConfigureAwait(false);

        return new DeviceFingerprint
        {
            Hostname = netbios?.Name,
            NameSource = netbios?.Name is null ? null : "NetBIOS",
            Workgroup = netbios?.Workgroup,
            Mac = mac,
            Vendor = vendor,
            DeviceKind = ClassifyKind(ports, vendor, banner),
            Banner = banner,
        };
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T?>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return default; }
    }

    internal static string? ClassifyKind(ISet<int> ports, string? vendor, string? banner)
    {
        bool V(string[] names) => vendor is not null && names.Any(n => vendor.Contains(n, StringComparison.OrdinalIgnoreCase));

        if (ports.Contains(9100) || ports.Contains(515) || ports.Contains(631) || (V(PrinterVendors) && !ports.Contains(445))) return "Принтер";
        if (ports.Contains(554) || V(CameraVendors)) return "Камера";
        if (ports.Contains(445) || ports.Contains(3389) || ports.Contains(135)) return "Windows";
        if (V(NetworkVendors)) return "Сетевое оборудование";
        if (banner is not null && banner.StartsWith("SSH-", StringComparison.Ordinal))
            return banner.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows" : "Linux/Unix";
        if (ports.Contains(22)) return "Linux/Unix";
        return null;
    }

    private async Task<ISet<int>?> OpenPortsAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        var tasks = KindPorts.Select(async p => (p, open: await _tcp.IsPortOpenAsync(ip, p, timeout, ct).ConfigureAwait(false))).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r.open).Select(r => r.p).ToHashSet();
    }

    private static async Task<string?> ReadSshBannerAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        var text = await ReadTcpAsync(ip, 22, null, timeout, ct).ConfigureAwait(false);
        var line = text?.Split('\n', 2)[0].Trim();
        return string.IsNullOrEmpty(line) ? null : Truncate(line);
    }

    private static async Task<string?> ReadHttpBannerAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        var text = await ReadTcpAsync(ip, 80, $"GET / HTTP/1.0\r\nHost: {ip}\r\nConnection: close\r\n\r\n", timeout, ct).ConfigureAwait(false);
        if (text is null) return null;
        var title = Regex.Match(text, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var server = Regex.Match(text, @"^Server:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var parts = new List<string>();
        if (title.Success && title.Groups[1].Value.Trim().Length > 0) parts.Add(Regex.Replace(title.Groups[1].Value.Trim(), @"\s+", " "));
        if (server.Success) parts.Add(server.Groups[1].Value);
        return parts.Count == 0 ? null : Truncate(string.Join(" · ", parts));
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120];

    private static async Task<string?> ReadTcpAsync(string ip, int port, string? request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout + timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(ip), port, cts.Token).ConfigureAwait(false);
        var stream = client.GetStream();
        if (request is not null) await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token).ConfigureAwait(false);
        var buffer = new byte[2048];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                if (request is null && Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Таймаут чтения: берём, что успело прийти.
        }

        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }
}

/// <summary>Запрос имён NetBIOS (NBSTAT, UDP 137): имя компьютера, рабочая группа и MAC сетевой карты.</summary>
internal static class NetBiosProbe
{
    internal sealed record Result(string? Name, string? Workgroup, string? Mac);

    private static readonly byte[] Query = BuildQuery();

    private static byte[] BuildQuery()
    {
        var q = new List<byte> { 0x52, 0x53, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x20 };
        q.AddRange(Encoding.ASCII.GetBytes("CK" + new string('A', 30)));
        q.AddRange(new byte[] { 0, 0, 0x21, 0, 1 });
        return q.ToArray();
    }

    public static async Task<Result?> QueryAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(Query, new IPEndPoint(IPAddress.Parse(ip), 137), ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var response = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return Parse(response.Buffer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static Result? Parse(byte[] data)
    {
        // Заголовок 12 байт + имя запроса 34 + тип/класс/TTL/длина 10 = число имён по смещению 56.
        const int countOffset = 56;
        if (data.Length <= countOffset) return null;
        var count = data[countOffset];
        var pos = countOffset + 1;
        if (data.Length < pos + count * 18) return null;

        string? name = null, workgroup = null;
        for (var i = 0; i < count; i++, pos += 18)
        {
            var text = Encoding.ASCII.GetString(data, pos, 15).Trim().TrimEnd('\0');
            var suffix = data[pos + 15];
            var isGroup = (data[pos + 16] & 0x80) != 0;
            if (suffix != 0 || text.Length == 0) continue;
            if (isGroup) workgroup ??= text;
            else name ??= text;
        }

        string? mac = null;
        if (data.Length >= pos + 6)
        {
            var bytes = data.AsSpan(pos, 6).ToArray();
            if (bytes.Any(b => b != 0)) mac = string.Join(':', bytes.Select(b => b.ToString("X2")));
        }

        return name is null && mac is null ? null : new Result(name, workgroup, mac);
    }
}
