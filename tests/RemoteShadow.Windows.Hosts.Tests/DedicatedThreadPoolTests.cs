using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RemoteShadow.Windows.Hosts.Tests;

public class DedicatedThreadPoolTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Runs_work_and_returns_result()
    {
        using var pool = new DedicatedThreadPool(2, 10);

        var result = await pool.RunAsync(() => 21 * 2, Long, CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Threads_are_background_and_named_wts_n()
    {
        using var pool = new DedicatedThreadPool(2, 10);

        var thread = await pool.RunAsync(() => Thread.CurrentThread, Long, CancellationToken.None);

        Assert.True(thread.IsBackground);
        Assert.False(thread.IsThreadPoolThread);
        Assert.Matches(new Regex(@"^wts-\d+$"), thread.Name);
    }

    [Fact]
    public async Task Parallelism_never_exceeds_thread_count()
    {
        const int threads = 3;
        using var pool = new DedicatedThreadPool(threads, 100);
        var current = 0;
        var max = 0;

        var tasks = Enumerable.Range(0, 20).Select(_ => pool.RunAsync(() =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref max, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref current);
            return 0;
        }, Long, CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        Assert.InRange(max, 2, threads);
    }

    [Fact]
    public async Task Deadline_returns_timeout_without_blocking_caller_while_worker_is_still_busy()
    {
        using var pool = new DedicatedThreadPool(1, 10);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finishedInBackground = new ManualResetEventSlim();

        var slow = pool.RunAsync(() =>
        {
            started.Set();
            release.Wait();
            finishedInBackground.Set();
            return 1;
        }, TimeSpan.FromMilliseconds(150), CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => slow);
        sw.Stop();

        Assert.True(started.IsSet);
        Assert.False(release.IsSet);
        Assert.False(finishedInBackground.IsSet);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "вызывающий не должен ждать, пока поток занят");

        // Поток всё ещё занят зависшим вызовом: следующая работа ждёт в очереди.
        var next = pool.RunAsync(() => 7, Long, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(next.IsCompleted);

        release.Set();
        Assert.Equal(7, await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(finishedInBackground.IsSet);
    }

    [Fact]
    public async Task Deadline_does_not_include_time_spent_waiting_in_queue()
    {
        using var pool = new DedicatedThreadPool(1, 10);
        using var release = new ManualResetEventSlim();

        var blocker = pool.RunAsync(() =>
        {
            release.Wait();
            return 0;
        }, Long, CancellationToken.None);

        // Дедлайн 300 мс, а в очереди работа простоит дольше: дедлайн считается только с начала выполнения.
        var queued = pool.RunAsync(() => "ok", TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await Task.Delay(600);
        Assert.False(queued.IsCompleted);

        release.Set();
        await blocker;
        Assert.Equal("ok", await queued.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Cancel_while_running_releases_caller_but_worker_finishes()
    {
        using var pool = new DedicatedThreadPool(1, 10);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        using var finished = new ManualResetEventSlim();

        var task = pool.RunAsync(() =>
        {
            started.Set();
            release.Wait();
            finished.Set();
            return 1;
        }, Long, cts.Token);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(finished.IsSet);

        release.Set();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Cancel_while_queued_skips_the_work()
    {
        using var pool = new DedicatedThreadPool(1, 10);
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        using var ran = new ManualResetEventSlim();

        var blocker = pool.RunAsync(() =>
        {
            release.Wait();
            return 0;
        }, Long, CancellationToken.None);

        var queued = pool.RunAsync(() =>
        {
            ran.Set();
            return 1;
        }, Long, cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        release.Set();
        await blocker;
        // Следом идёт контрольная работа: она выполняется после пропущенной, значит пропущенная уже разобрана.
        await pool.RunAsync(() => 0, Long, CancellationToken.None);

        Assert.False(ran.IsSet);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_immediately()
    {
        using var pool = new DedicatedThreadPool(1, 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.RunAsync(() => 1, Long, cts.Token));
    }

    [Fact]
    public async Task Work_exception_is_propagated_unchanged()
    {
        using var pool = new DedicatedThreadPool(1, 10);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.RunAsync<int>(() => throw new InvalidOperationException("boom"), Long, CancellationToken.None));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Full_queue_makes_caller_wait_for_a_slot()
    {
        using var pool = new DedicatedThreadPool(1, 1);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var first = pool.RunAsync(() =>
        {
            started.Set();
            release.Wait();
            return 1;
        }, Long, CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        var second = pool.RunAsync(() => 2, Long, CancellationToken.None); // занимает единственное место очереди
        var third = pool.RunAsync(() => 3, Long, CancellationToken.None);  // ждёт свободного места
        await Task.Delay(150);
        Assert.False(third.IsCompleted);
        Assert.Equal(1, pool.PendingCount);

        release.Set();
        Assert.Equal(new[] { 1, 2, 3 }, await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Dispose_fails_pending_work_and_rejects_new()
    {
        var pool = new DedicatedThreadPool(1, 10);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var running = pool.RunAsync(() =>
        {
            started.Set();
            release.Wait();
            return 1;
        }, Long, CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var pending = pool.RunAsync(() => 2, Long, CancellationToken.None);
        await Task.Delay(50);

        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RunAsync(() => 3, Long, CancellationToken.None));

        release.Set();
        Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DedicatedThreadPool(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DedicatedThreadPool(1, 0));

        using var pool = new DedicatedThreadPool(1, 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pool.RunAsync(() => 1, TimeSpan.Zero, CancellationToken.None));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen) return;
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
