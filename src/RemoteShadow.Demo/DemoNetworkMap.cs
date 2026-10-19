using System.Collections.Concurrent;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo;

/// <summary>
/// Соответствие «имя хоста ↔ IP-адрес» для демо-режима: детерминированное, без единого реального DNS/сокет-вызова.
/// Общая на все демо-сервисы (DNS, обратный DNS, ICMP, сканер подсети), чтобы они видели одну и ту же картину сети.
/// Адреса назначаются по порядку обращения из офисной подсети <see cref="OfficeCidr"/> — этого хватает с запасом
/// на демо-набор компьютеров (по умолчанию 230) плюс несколько «лишних» устройств вне AD.
/// </summary>
public sealed class DemoNetworkMap
{
    public const string OfficeCidr = "10.50.0.0/22"; // 1022 адреса, с запасом

    private readonly IReadOnlyList<string> _addresses = Cidr.Parse(OfficeCidr).UsableAddresses().Select(a => a.ToString()).ToList();
    private readonly ConcurrentDictionary<string, string> _ipByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _hostByIp = new(StringComparer.OrdinalIgnoreCase);
    private int _next = 10; // первые адреса «заняты» шлюзом/принтерами, как в настоящей сети

    /// <summary>IP-адрес для хоста; при первом обращении назначается следующий свободный из <see cref="OfficeCidr"/>.</summary>
    public string IpFor(string host) => _ipByHost.GetOrAdd(host, h =>
    {
        var index = Interlocked.Increment(ref _next) % _addresses.Count;
        var ip = _addresses[index];
        _hostByIp[ip] = h;
        return ip;
    });

    /// <summary>Имя хоста по IP, если он уже был выдан через <see cref="IpFor"/>; иначе null (устройство вне AD).</summary>
    public string? HostFor(string ip) => _hostByIp.GetValueOrDefault(ip);
}
