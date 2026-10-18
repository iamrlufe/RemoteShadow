using System.Net.Sockets;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Проверка доступности хоста TCP-подключением. ICMP не используем: он часто закрыт.</summary>
public sealed class TcpHostProbe : IHostProbe
{
    /// <summary>SMB/RPC: открыт у любого компьютера, которым можно управлять, поэтому порт и считается признаком «в сети».</summary>
    public const int OnlinePort = 445;

    /// <summary>
    /// true — порт принимает подключения. Разрешение имени входит в <paramref name="timeout"/>.
    /// Любая сетевая ошибка и истечение таймаута → false; отмена вызывающего → <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return false;
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // Отмену вызывающего возвращаем как отмену, а не как «порт закрыт».
            ct.ThrowIfCancellationRequested();

            // Таймаут (отмена нашего источника), отказ в подключении, DNS не разрешил имя и прочие сетевые сбои —
            // для опроса это всё один ответ: порт недоступен.
            return false;
        }
    }

    public Task<bool> IsOnlineAsync(string host, TimeSpan timeout, CancellationToken ct) =>
        IsPortOpenAsync(host, OnlinePort, timeout, ct);
}
