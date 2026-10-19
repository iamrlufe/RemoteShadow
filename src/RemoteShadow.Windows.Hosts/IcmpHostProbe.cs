using System.Net.NetworkInformation;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Проверка доступности узла по ICMP (ping), через System.Net.NetworkInformation.Ping.</summary>
public sealed class IcmpHostProbe : IIcmpProbe
{
    public async Task<bool> PingAsync(string hostOrIp, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(hostOrIp)) return false;

        var timeoutMs = timeout == Timeout.InfiniteTimeSpan ? int.MaxValue : (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);

        try
        {
            using var ping = new Ping();
            using var registration = ct.Register(static p => ((Ping)p!).SendAsyncCancel(), ping);

            var reply = await ping.SendPingAsync(hostOrIp, timeoutMs).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // PingException (имя не резолвится, нет маршрута), фаервол без ICMP и т. п. — для опроса это «не ответил».
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
