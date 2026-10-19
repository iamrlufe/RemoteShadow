using System.IO;
using RemoteShadow.App.Shell;

namespace RemoteShadow.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void Second_instance_for_the_same_data_dir_is_refused_until_the_first_exits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-guard-" + Guid.NewGuid().ToString("N"));

        using var first = SingleInstanceGuard.TryAcquire(dir);
        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(dir));
        Assert.Null(SingleInstanceGuard.TryAcquire(dir.ToUpperInvariant() + "\\"));

        first!.Dispose();

        using var again = SingleInstanceGuard.TryAcquire(dir);
        Assert.NotNull(again);
    }

    [Fact]
    public void Different_data_dirs_do_not_block_each_other()
    {
        using var a = SingleInstanceGuard.TryAcquire(Path.Combine(Path.GetTempPath(), "rs-guard-a-" + Guid.NewGuid().ToString("N")));
        using var b = SingleInstanceGuard.TryAcquire(Path.Combine(Path.GetTempPath(), "rs-guard-b-" + Guid.NewGuid().ToString("N")));

        Assert.NotNull(a);
        Assert.NotNull(b);
    }
}
