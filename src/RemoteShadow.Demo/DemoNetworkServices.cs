using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>Резолвинг DNS в демо-режиме: детерминированный, через <see cref="DemoNetworkMap"/>, без System.Net.Dns.</summary>
public sealed class DemoDnsResolverService : IDnsResolverService
{
    private readonly DemoData _data;
    private readonly DemoNetworkMap _map;
    private readonly DemoTimings _timings;

    public DemoDnsResolverService(DemoData data, DemoNetworkMap map, DemoTimings? timings = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _timings = timings ?? DemoTimings.Default;
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var computer = _data.FindComputerByHost(host);
        var delay = DemoDelay.ByKey($"{host}:dns", TimeSpan.Zero, _timings.ProbeMax);
        await DemoDelay.Wait(delay, ct).ConfigureAwait(false);

        if (computer is null) return Array.Empty<string>();
        return new[] { _map.IpFor(computer.HostName) };
    }
}

/// <summary>Обратный DNS в демо-режиме: по тому же <see cref="DemoNetworkMap"/>, без System.Net.Dns.</summary>
public sealed class DemoReverseDnsResolver : IReverseDnsResolver
{
    private readonly DemoNetworkMap _map;
    private readonly DemoTimings _timings;

    public DemoReverseDnsResolver(DemoNetworkMap map, DemoTimings? timings = null)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _timings = timings ?? DemoTimings.Default;
    }

    public async Task<string?> ResolveAsync(string ipAddress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var delay = DemoDelay.ByKey($"{ipAddress}:ptr", TimeSpan.Zero, _timings.ProbeMax);
        await DemoDelay.Wait(delay, ct).ConfigureAwait(false);

        return _map.HostFor(ipAddress);
    }
}

/// <summary>
/// ICMP-пинг в демо-режиме: отвечают компьютеры, которые «в сети» в <see cref="DemoData"/>; для адресов, не связанных
/// ни с одним демо-компьютером (при сканировании подсети), отвечает примерно четверть — имитация посторонних устройств.
/// Никаких реальных ICMP-пакетов не отправляется.
/// </summary>
public sealed class DemoIcmpProbe : IIcmpProbe
{
    private readonly DemoData _data;
    private readonly DemoNetworkMap _map;
    private readonly DemoTimings _timings;

    public DemoIcmpProbe(DemoData data, DemoNetworkMap map, DemoTimings? timings = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _timings = timings ?? DemoTimings.Default;
    }

    public async Task<bool> PingAsync(string hostOrIp, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var computer = _data.FindComputerByHost(hostOrIp) ?? ByReverseIp(hostOrIp);
        var key = $"{computer?.Name ?? hostOrIp}:icmp";
        var delay = DemoDelay.ByKey(key, TimeSpan.Zero, _timings.ProbeMax);

        if (delay > timeout)
        {
            await DemoDelay.Wait(timeout, ct).ConfigureAwait(false);
            return false;
        }

        await DemoDelay.Wait(delay, ct).ConfigureAwait(false);

        if (computer is not null) return _data.IsOnline(computer.Id);
        return DemoRandom.Hash(hostOrIp) % 4 == 0;
    }

    private ComputerInfo? ByReverseIp(string ip) => _map.HostFor(ip) is { } host ? _data.FindComputerByHost(host) : null;
}
