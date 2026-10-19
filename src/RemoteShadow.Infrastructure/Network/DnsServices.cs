using System.Net;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Резолвинг DNS-имени компьютера в IP-адреса (System.Net.Dns). DNS-запись сама по себе не означает, что компьютер
/// доступен — это отдельная последующая проверка (<see cref="IComputerAvailabilityService"/>).
/// </summary>
public sealed class DnsResolverService : IDnsResolverService
{
    public async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(host)) return Array.Empty<string>();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) // SocketException (имя не найдено), ArgumentException (пустое/недопустимое имя) и т. п.
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>Обратный DNS (PTR) через System.Net.Dns.GetHostEntryAsync.</summary>
public sealed class ReverseDnsResolver : IReverseDnsResolver
{
    public async Task<string?> ResolveAsync(string ipAddress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IPAddress.TryParse(ipAddress, out var ip)) return null;

        try
        {
            var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(ct).ConfigureAwait(false);
            var name = entry.HostName;

            // Без PTR-записи Windows может вернуть вместо имени сам адрес: это не имя, сопоставлять по нему нечего.
            if (string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name, out _)) return null;
            return name;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // Нет PTR-записи или DNS недоступен — для нас это «неоднозначно», см. интерфейс.
        }
    }
}
