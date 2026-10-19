using System.Net;
using System.Runtime.CompilerServices;
using RemoteShadow.Core;

namespace RemoteShadow.Windows.Hosts.Tests;

/// <summary>
/// Ошибка работы, которую вызывающий перестал ждать (отмена, дедлайн), не должна всплывать в
/// TaskScheduler.UnobservedTaskException: приложение показывает на это окно «ошибка фоновой задачи».
/// </summary>
[Collection(UnobservedFaultCollection.Name)]
public class UnobservedFaultTests
{
    [Fact]
    public async Task Pool_work_failing_after_caller_cancelled_is_observed()
    {
        var marker = "pool-" + Guid.NewGuid().ToString("N");
        await AssertNotUnobservedAsync(marker, () => PoolCancelAndFailAsync(marker));
    }

    [Fact]
    public async Task Cim_work_failing_after_deadline_is_observed()
    {
        var marker = "cim-" + Guid.NewGuid().ToString("N");
        await AssertNotUnobservedAsync(marker, () => CimTimeoutAndFailAsync(marker));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PoolCancelAndFailAsync(string marker)
    {
        using var pool = new DedicatedThreadPool(1, 4);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var finished = new TaskCompletionSource();

        var run = pool.RunAsync<int>(() =>
        {
            started.Set();
            release.Wait();
            try
            {
                throw new InvalidOperationException(marker);
            }
            finally
            {
                finished.TrySetResult();
            }
        }, TimeSpan.FromSeconds(30), cts.Token);

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        release.Set();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CimTimeoutAndFailAsync(string marker)
    {
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource();
        var service = new CimDiagnosticsService(
            TestConnections.LoggedIn(new FakeConnection()),
            new FakeProbe(),
            (_, _) =>
            {
                release.Wait();
                try
                {
                    throw new CimRemoteException(CimFailureKind.Connection, marker);
                }
                finally
                {
                    finished.TrySetResult();
                }
            },
            (_, _) => Task.FromResult<IPAddress[]>([]),
            TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => service.FixAsync(TestComputers.Pc(), "start-termservice", CancellationToken.None));
        Assert.Equal(OperationFailure.Timeout, ex.Failure);

        release.Set();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
    }

    private static async Task AssertNotUnobservedAsync(string marker, Func<Task> scenario)
    {
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message.Contains(marker, StringComparison.Ordinal)))
                Interlocked.Increment(ref unobserved);
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await scenario();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UnobservedFaultCollection
{
    public const string Name = "Unobserved task faults";
}
