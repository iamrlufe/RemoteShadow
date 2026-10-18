using System.Collections.Concurrent;

namespace RemoteShadow.Windows.Hosts;

/// <summary>
/// Собственный ограниченный пул потоков для блокирующих вызовов, которые нельзя отменить (WTS, RPC).
/// Это не пул .NET: зависшие вызовы не должны съедать его потоки. Потоки фоновые, с именами «wts-N».
/// <para>
/// Дедлайн отсчитывается с момента, когда работа начала выполняться на потоке: время ожидания в очереди в него
/// не входит, иначе хвост большой пачки хостов получал бы «не отвечает», так и не дойдя до опроса. Если дедлайн истёк,
/// вызывающий получает <see cref="TimeoutException"/> сразу, а поток дорабатывает вызов в фоне, и результат отбрасывается.
/// Отмена токена прерывает только ожидание вызывающего; работа, не начатая к этому моменту, пропускается.
/// </para>
/// </summary>
internal sealed class DedicatedThreadPool : IDisposable
{
    private readonly ConcurrentQueue<IWorkItem> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly SemaphoreSlim _freeSlots;
    private readonly Thread[] _threads;
    private volatile bool _disposed;

    /// <param name="threadCount">Сколько потоков (больше нуля).</param>
    /// <param name="queueLimit">Сколько работ может ждать начала выполнения. При переполнении <see cref="RunAsync{T}"/> ждёт места.</param>
    /// <param name="threadNamePrefix">Имена потоков: «prefix-1», «prefix-2»…</param>
    public DedicatedThreadPool(int threadCount, int queueLimit, string threadNamePrefix = "wts")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueLimit, 1);

        ThreadCount = threadCount;
        _freeSlots = new SemaphoreSlim(queueLimit, queueLimit);
        _threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            _threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"{threadNamePrefix}-{i + 1}",
            };
            _threads[i].Start();
        }
    }

    public int ThreadCount { get; }

    /// <summary>Сколько работ ждёт начала выполнения.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Выполняет <paramref name="work"/> на потоке пула. <see cref="TimeoutException"/> — не уложились в <paramref name="deadline"/>;
    /// <see cref="OperationCanceledException"/> — отменил вызывающий; исключение самой работы пробрасывается как есть.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> work, TimeSpan deadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (deadline != Timeout.InfiniteTimeSpan && deadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(deadline), "Дедлайн должен быть положительным.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Место в очереди занимаем асинхронно: при переполнении вызывающий ждёт, а не получает ошибку.
        await _freeSlots.WaitAsync(ct).ConfigureAwait(false);

        var item = new WorkItem<T>(work, deadline);
        _queue.Enqueue(item);
        _available.Release();
        if (_disposed) DrainQueue();

        try
        {
            return await item.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Работа, которая ещё стоит в очереди, не нужна никому: поток её пропустит. Уже идущую не прервать.
            item.CancelIfQueued();
            throw;
        }
    }

    public Task RunAsync(Action work, TimeSpan deadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(() =>
        {
            work();
            return true;
        }, deadline, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _available.Release(ThreadCount);
        DrainQueue();
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var item))
        {
            _freeSlots.Release();
            item.Fail(new ObjectDisposedException(nameof(DedicatedThreadPool)));
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _available.Wait();
            if (_disposed) return;
            if (!_queue.TryDequeue(out var item)) continue;

            _freeSlots.Release();
            item.Run();
        }
    }

    private interface IWorkItem
    {
        void Run();
        void CancelIfQueued();
        void Fail(Exception exception);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Cancelled = 2;

        private readonly Func<T> _work;
        private readonly TimeSpan _deadline;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;

        public WorkItem(Func<T> work, TimeSpan deadline)
        {
            _work = work;
            _deadline = deadline;
        }

        public Task<T> Task => _completion.Task;

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued) return;

            // Таймер только освобождает вызывающего; поток продолжает работу, пока вызов не вернётся сам.
            Timer? timer = null;
            if (_deadline != Timeout.InfiniteTimeSpan)
            {
                timer = new Timer(
                    static state => ((WorkItem<T>)state!)._completion.TrySetException(new TimeoutException("Превышено время ожидания ответа.")),
                    this,
                    _deadline,
                    Timeout.InfiniteTimeSpan);
            }

            try
            {
                _completion.TrySetResult(_work());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                timer?.Dispose();
            }
        }

        public void CancelIfQueued()
        {
            if (Interlocked.CompareExchange(ref _state, Cancelled, Queued) == Queued)
                _completion.TrySetCanceled();
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, Cancelled, Queued) == Queued)
                _completion.TrySetException(exception);
        }
    }
}
