using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows.Hosts;

/// <summary>
/// MAC по IP через iphlpapi!SendARP. Работает только для адресов своей L2-сети: для адреса за маршрутизатором или VPN
/// SendARP «успешно» возвращает MAC шлюза (у всех устройств подсети один и тот же), поэтому такие адреса отсекаются заранее.
/// </summary>
public sealed class ArpResolver : IArpResolver
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

    public string? TryGetMac(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        if (!IsOnLink(ip)) return null;
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

    /// <summary>Адрес лежит в подсети одного из локальных Ethernet/Wi-Fi интерфейсов (не VPN/PPP/туннель).</summary>
    private static bool IsOnLink(IPAddress target)
    {
        try
        {
            var t = target.GetAddressBytes();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.GigabitEthernet
 or NetworkInterfaceType.Wireless80211)) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var a = ua.Address.GetAddressBytes();
                    var m = ua.IPv4Mask.GetAddressBytes();
                    var same = true;
                    for (var i = 0; i < 4 && same; i++) same = (a[i] & m[i]) == (t[i] & m[i]);
                    if (same) return true;
                }
            }
        }
        catch
        {
            // Нет данных об интерфейсах — MAC просто не показываем.
        }

        return false;
    }
}
