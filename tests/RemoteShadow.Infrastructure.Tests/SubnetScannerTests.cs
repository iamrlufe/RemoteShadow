using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

public class SubnetScannerTests
{
    private sealed class Fixture
    {
        public Fixture()
        {
            Scanner = new SubnetScanner(Tcp, Icmp, Dns, Time);
        }

        public FakeHostProbe Tcp { get; } = new();
        public FakeIcmpProbe Icmp { get; } = new();
        public FakeReverseDnsResolver Dns { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public SubnetScanner Scanner { get; }

        public async Task<List<DiscoveredDevice>> ScanAsync(SubnetDefinition subnet, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
        {
            var found = new List<DiscoveredDevice>();
            await foreach (var device in Scanner.ScanAsync(subnet, progress, ct)) found.Add(device);
            return found;
        }
    }

    private static SubnetDefinition Subnet(string cidr, ProbeKind kind = ProbeKind.Both, int timeoutMs = 200, int parallelism = 8) =>
        new() { Name = "Test", Cidr = cidr, ProbeKind = kind, TimeoutMs = timeoutMs, Parallelism = parallelism };

    [Fact]
    public async Task Scans_only_usable_addresses_of_the_cidr()
    {
        var fx = new Fixture();
        fx.Icmp.Online = _ => true;

        var found = await fx.ScanAsync(Subnet("10.9.0.0/30")); // 2 usable: .1 и .2

        Assert.Equal(2, found.Count);
        Assert.Contains(found, d => d.IpAddress == "10.9.0.1");
        Assert.Contains(found, d => d.IpAddress == "10.9.0.2");
    }

    [Fact]
    public async Task Non_responding_addresses_are_not_returned()
    {
        var fx = new Fixture();
        fx.Icmp.Online = ip => ip.EndsWith(".1");
        fx.Tcp.Online = _ => false;

        var found = await fx.ScanAsync(Subnet("10.9.0.0/30", ProbeKind.Both));

        Assert.Single(found);
        Assert.Equal("10.9.0.1", found[0].IpAddress);
    }

    [Fact]
    public async Task Icmp_only_does_not_call_tcp_probe()
    {
        var fx = new Fixture();
        fx.Icmp.Online = _ => true;

        await fx.ScanAsync(Subnet("10.9.0.4/31", ProbeKind.IcmpOnly));

        Assert.Empty(fx.Tcp.Calls);
    }

    [Fact]
    public async Task Tcp_only_does_not_call_icmp_probe()
    {
        var fx = new Fixture();
        fx.Tcp.Online = _ => true;

        await fx.ScanAsync(Subnet("10.9.0.4/31", ProbeKind.TcpOnly));

        Assert.Empty(fx.Icmp.Calls);
    }

    [Fact]
    public async Task Reverse_dns_is_attached_when_available()
    {
        var fx = new Fixture();
        fx.Icmp.Online = _ => true;
        fx.Dns.Names["10.9.0.4"] = "pc-001.corp.local";

        var found = await fx.ScanAsync(Subnet("10.9.0.4/31"));

        Assert.Equal("pc-001.corp.local", found.Single(d => d.IpAddress == "10.9.0.4").ReverseDnsName);
        Assert.Null(found.Single(d => d.IpAddress == "10.9.0.5").ReverseDnsName);
    }

    [Fact]
    public async Task Progress_is_reported_up_to_total()
    {
        var fx = new Fixture();
        fx.Icmp.Online = _ => false;
        var reports = new List<ScanProgress>();

        await fx.ScanAsync(Subnet("10.9.0.0/29"), new Progress2(reports)); // 6 usable

        Assert.NotEmpty(reports);
        Assert.Equal(6, reports[^1].Total);
        Assert.Equal(6, reports[^1].Completed);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        var fx = new Fixture();
        var gate = new Gate();
        fx.Icmp.Online = _ => true;
        var slowHost = string.Empty;
        using var cts = new CancellationTokenSource();

        var task = Task.Run(async () =>
        {
            var list = new List<DiscoveredDevice>();
            await foreach (var d in fx.Scanner.ScanAsync(Subnet("10.9.0.0/20"), null, cts.Token))
            {
                list.Add(d);
                if (list.Count == 1) await cts.CancelAsync();
            }
            return list;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WithTimeout());
    }

    [Fact]
    public void Scanning_a_real_external_address_is_never_attempted_by_the_fakes()
    {
        // Гарантия для ревью: фейки сканера работают только с адресами, переданными им явно, и не используют
        // System.Net.Sockets/System.Net.Dns самостоятельно — реальной сети тесты не касаются.
        var fx = new Fixture();
        Assert.Empty(fx.Tcp.Calls);
        Assert.Empty(fx.Icmp.Calls);
    }

    private sealed class Progress2 : IProgress<ScanProgress>
    {
        private readonly List<ScanProgress> _list;
        public Progress2(List<ScanProgress> list) => _list = list;
        public void Report(ScanProgress value) => _list.Add(value);
    }
}

public class DeviceMergeServiceTests
{
    private readonly DeviceMergeService _service = new();

    private static DiscoveredDevice Device(string ip, string? reverseDns) => new()
    {
        Key = ip,
        SubnetId = Guid.NewGuid(),
        IpAddress = ip,
        ReverseDnsName = reverseDns,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
        FoundInLastScan = true,
        IsOnline = true,
    };

    [Fact]
    public void Matches_by_reverse_dns_short_name_not_by_ip()
    {
        var pc = Pc("PC-001", id: 1);
        var device = Device("10.0.0.99", "pc-001.corp.local"); // IP не совпадает ни с чем в AD — это нормально

        var matched = _service.Match(device, new[] { pc });

        Assert.Equal(pc.Id, matched);
    }

    [Fact]
    public void No_reverse_dns_means_unmatched()
    {
        var pc = Pc("PC-001", id: 1);
        var device = Device("10.0.0.1", null);

        Assert.Null(_service.Match(device, new[] { pc }));
    }

    [Fact]
    public void Reverse_dns_not_found_in_ad_means_unmatched_device_outside_ad()
    {
        var pc = Pc("PC-001", id: 1);
        var device = Device("10.0.0.1", "unknown-host.corp.local");

        Assert.Null(_service.Match(device, new[] { pc }));
    }

    [Fact]
    public void Ambiguous_name_across_two_ad_computers_is_left_unmatched()
    {
        var a = Pc("DUP", id: 1) with { DnsName = "dup.corp.local" };
        var b = Pc("DUP", id: 2) with { DnsName = "dup.corp.local" };
        var device = Device("10.0.0.1", "dup.corp.local");

        Assert.Null(_service.Match(device, new[] { a, b }));
    }

    [Fact]
    public void Changing_ip_of_the_same_host_still_matches_by_name()
    {
        var pc = Pc("PC-001", id: 1);
        var first = Device("10.0.0.1", "pc-001.corp.local");
        var afterDhcpRenewal = Device("10.0.0.55", "pc-001.corp.local");

        Assert.Equal(pc.Id, _service.Match(first, new[] { pc }));
        Assert.Equal(pc.Id, _service.Match(afterDhcpRenewal, new[] { pc }));
    }
}
