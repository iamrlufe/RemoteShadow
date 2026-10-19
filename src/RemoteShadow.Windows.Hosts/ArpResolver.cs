using System.Net;
using System.Runtime.InteropServices;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows.Hosts;

/// <summary>MAC по IP через iphlpapi!SendARP. Работает только для адресов своей L2-сети.</summary>
public sealed class ArpResolver : IArpResolver
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

    public string? TryGetMac(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        try
        {
            var mac = new byte[6];
            var len = (uint)mac.Length;
            var dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            if (SendARP(dest, 0, mac, ref len) != 0 || len != 6) return null;
            return string.Join(':', mac.Select(b => b.ToString("X2")));
        }
        catch
        {
            return null;
        }
    }
}
