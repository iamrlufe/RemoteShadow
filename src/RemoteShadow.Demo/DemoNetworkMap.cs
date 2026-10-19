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
    private readonly object _assignGate = new();
    private int _next = 10; // первые адреса «заняты» шлюзом/принтерами, как в настоящей сети

    /// <summary>IP-адрес для хоста; при первом обращении назначается следующий свободный из <see cref="OfficeCidr"/>.</summary>
    public string IpFor(string host)
    {
        if (_ipByHost.TryGetValue(host, out var known)) return known;

        // GetOrAdd может вызвать фабрику дважды для одного хоста при одновременных запросах: тогда хосту доставались бы
        // два адреса, и «лишний» отвечал бы его именем в обратном DNS. Назначение — под блокировкой.
        lock (_assignGate)
        {
            if (_ipByHost.TryGetValue(host, out known)) return known;

            var ip = _addresses[++_next % _addresses.Count];
            _hostByIp[ip] = host;
            _ipByHost[host] = ip;
            return ip;
        }
    }

    /// <summary>Имя хоста по IP, если он уже был выдан через <see cref="IpFor"/>; иначе null (устройство вне AD).</summary>
    public string? HostFor(string ip) => _hostByIp.GetValueOrDefault(ip);
}
