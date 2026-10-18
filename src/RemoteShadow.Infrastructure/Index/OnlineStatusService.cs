using System.Collections.Concurrent;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Статус «в сети» (порт 445) для переданных компьютеров. Результаты не старше <see cref="AppSettings.OnlineCacheSeconds"/>
/// не проверяются повторно; одновременно идёт не больше <see cref="AppSettings.OnlineProbeParallelism"/> проверок;
/// результаты применяются к индексу пачками раз в 200 мс. Отключённые компьютеры пропускаются, ошибка проверки хоста
/// означает «не в сети». Отмена прекращает проверки, применяет уже полученные результаты и бросает <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class OnlineStatusService : IOnlineStatusService
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    private readonly IComputerIndex _index;
    private readonly IHostProbe _probe;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, DateTime> _checkedAt = new();

    public OnlineStatusService(IComputerIndex index, IHostProbe probe, ISettingsStore settings, TimeProvider time)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task RefreshAsync(IReadOnlyCollection<ComputerInfo> computers, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computers);
        ct.ThrowIfCancellationRequested();

        var settings = _settings.Load();
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(settings.OnlineProbeTimeoutMs, 50, 30_000));
        var parallelism = Math.Clamp(settings.OnlineProbeParallelism, 1, 512);
        var cacheTtl = TimeSpan.FromSeconds(Math.Max(0, settings.OnlineCacheSeconds));

        var todo = SelectStale(computers, force, cacheTtl);
        if (todo.Count == 0) return;

        var results = new ConcurrentQueue<KeyValuePair<Guid, bool>>();
        using var stopFlusher = new CancellationTokenSource();
        var flusher = FlushPeriodicallyAsync(results, stopFlusher.Token);

        try
        {
            var next = -1;
            var workers = Enumerable.Range(0, Math.Min(parallelism, todo.Count))
                .Select(_ => Task.Run(() => WorkAsync(todo, timeout, results, () => Interlocked.Increment(ref next), ct), CancellationToken.None))
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            await stopFlusher.CancelAsync().ConfigureAwait(false);
            await flusher.ConfigureAwait(false);
            Flush(results);
        }

        ct.ThrowIfCancellationRequested();
    }

    private List<ComputerInfo> SelectStale(IReadOnlyCollection<ComputerInfo> computers, bool force, TimeSpan cacheTtl)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var seen = new HashSet<Guid>();
        var todo = new List<ComputerInfo>(computers.Count);

        foreach (var computer in computers)
        {
            if (!computer.Enabled || !seen.Add(computer.Id)) continue;

            if (!force && _checkedAt.TryGetValue(computer.Id, out var checkedAt) && now - checkedAt < cacheTtl)
            {
                // Индекс могли очистить (смена учётной записи): тогда статус потерян и проверять нужно заново.
                var known = _index.Find(computer.Id)?.IsOnline ?? computer.IsOnline;
                if (known is not null) continue;
            }

            todo.Add(computer);
        }

        return todo;
    }

    private async Task WorkAsync(
        List<ComputerInfo> todo,
        TimeSpan timeout,
        ConcurrentQueue<KeyValuePair<Guid, bool>> results,
        Func<int> takeNext,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var i = takeNext();
            if (i >= todo.Count) return;

            var computer = todo[i];
            bool online;
            try
            {
                // Имя уходит в DNS и сокет; явно некорректное имя не проверяем, а считаем недоступным.
                online = HostNameValidator.IsValid(computer.HostName)
                         && await _probe.IsOnlineAsync(computer.HostName, timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                online = false;
            }

            _checkedAt[computer.Id] = _time.GetUtcNow().UtcDateTime;
            results.Enqueue(new KeyValuePair<Guid, bool>(computer.Id, online));
        }
    }

    private async Task FlushPeriodicallyAsync(ConcurrentQueue<KeyValuePair<Guid, bool>> results, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(FlushInterval, _time, stop).ConfigureAwait(false);
                Flush(results);
            }
        }
        catch (OperationCanceledException)
        {
            // Работа закончилась — остаток применит вызывающий.
        }
    }

    private void Flush(ConcurrentQueue<KeyValuePair<Guid, bool>> results)
    {
        while (results.TryDequeue(out var result))
        {
            _index.SetOnline(result.Key, result.Value);
        }
    }
}
