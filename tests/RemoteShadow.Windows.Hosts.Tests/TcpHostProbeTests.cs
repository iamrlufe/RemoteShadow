using System.Net;
using System.Net.Sockets;

namespace RemoteShadow.Windows.Hosts.Tests;

public class TcpHostProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly TcpHostProbe _probe = new();

    [Fact]
    public async Task Open_port_on_local_listener_is_true()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(await _probe.IsPortOpenAsync("127.0.0.1", port, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Closed_port_is_false()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.False(await _probe.IsPortOpenAsync("127.0.0.1", port, Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task Localhost_name_is_resolved_inside_the_probe()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(await _probe.IsPortOpenAsync("localhost", port, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_host_is_false(string host) =>
        Assert.False(await _probe.IsPortOpenAsync(host, 445, Timeout, CancellationToken.None));

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public async Task Invalid_port_is_false(int port) =>
        Assert.False(await _probe.IsPortOpenAsync("127.0.0.1", port, Timeout, CancellationToken.None));

    [Fact]
    public async Task Zero_timeout_is_false_without_connecting()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.False(await _probe.IsPortOpenAsync("127.0.0.1", port, TimeSpan.Zero, CancellationToken.None));
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Cancelled_token_throws_operation_canceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _probe.IsPortOpenAsync("127.0.0.1", 445, Timeout, cts.Token));
    }

    [Fact]
    public async Task IsOnline_checks_port_445()
    {
        Assert.Equal(445, TcpHostProbe.OnlinePort);

        var viaOnline = await _probe.IsOnlineAsync("127.0.0.1", Timeout, CancellationToken.None);
        var viaPort = await _probe.IsPortOpenAsync("127.0.0.1", 445, Timeout, CancellationToken.None);

        Assert.Equal(viaPort, viaOnline);
    }
}
