using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>
/// Тестовая проверка доступности: «в сети» значит то же, что в <see cref="DemoData"/>. Включённые компьютеры
/// отвечают на типовые порты Windows (135, 139, 445, 3389, 5985), примерно каждый пятый — ещё и на 22 (ssh).
/// Задержка от 0 до <see cref="DemoTimings.ProbeMax"/>, зависит от имени хоста и порта.
/// </summary>
public sealed class DemoHostProbe : IHostProbe
{
    public const int SmbPort = 445;

    private static readonly HashSet<int> WindowsPorts = [135, 139, SmbPort, 3389, 5985];

    private readonly DemoData _data;
    private readonly DemoTimings _timings;

    public DemoHostProbe(DemoData data, DemoTimings? timings = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _timings = timings ?? DemoTimings.Default;
    }

    public async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var computer = _data.FindComputerByHost(host);
        var key = $"{computer?.Name ?? host}:{port}";
        var delay = DemoDelay.ByKey(key, TimeSpan.Zero, _timings.ProbeMax);

        if (delay > timeout)
        {
            await DemoDelay.Wait(timeout, ct).ConfigureAwait(false);
            return false;
        }

        await DemoDelay.Wait(delay, ct).ConfigureAwait(false);

        if (computer is null || !_data.IsOnline(computer.Id)) return false;
        return WindowsPorts.Contains(port) || (port == 22 && DemoRandom.Hash(computer.Name) % 5 == 0);
    }

    public Task<bool> IsOnlineAsync(string host, TimeSpan timeout, CancellationToken ct) =>
        IsPortOpenAsync(host, SmbPort, timeout, ct);
}
