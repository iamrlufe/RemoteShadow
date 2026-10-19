using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo.Tests;

/// <summary>
/// Демо-сервисы доступности/сканирования: детерминированные, без реального DNS/ICMP/сокетов. Заодно проверяет
/// требование безопасности: демо-режим не делает реальных сетевых вызовов (конструктивно — реализации работают
/// только с DemoData/DemoNetworkMap в памяти, System.Net.Dns/Sockets тут нигде не используются).
/// </summary>
public class DemoNetworkServicesTests
{
    [Fact]
    public async Task Dns_resolver_returns_an_ip_only_for_known_computers()
    {
        var data = TestSupport.Data();
        var map = new DemoNetworkMap();
        var dns = new DemoDnsResolverService(data, map, DemoTimings.Instant);
        var pc = data.GetComputers().First();

        var addresses = await dns.ResolveAsync(pc.HostName, CancellationToken.None);
        var unknown = await dns.ResolveAsync("no-such-host.corp.local", CancellationToken.None);

        Assert.Single(addresses);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task Dns_resolution_is_stable_across_calls()
    {
        var data = TestSupport.Data();
        var map = new DemoNetworkMap();
        var dns = new DemoDnsResolverService(data, map, DemoTimings.Instant);
        var pc = data.GetComputers().First();

        var first = await dns.ResolveAsync(pc.HostName, CancellationToken.None);
        var second = await dns.ResolveAsync(pc.HostName, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Reverse_dns_matches_forward_dns()
    {
        var data = TestSupport.Data();
        var map = new DemoNetworkMap();
        var dns = new DemoDnsResolverService(data, map, DemoTimings.Instant);
        var reverse = new DemoReverseDnsResolver(map, DemoTimings.Instant);
        var pc = data.GetComputers().First();

        var ip = (await dns.ResolveAsync(pc.HostName, CancellationToken.None)).Single();
        var name = await reverse.ResolveAsync(ip, CancellationToken.None);

        Assert.Equal(pc.HostName, name);
    }

    [Fact]
    public async Task Reverse_dns_of_never_assigned_ip_is_null()
    {
        var reverse = new DemoReverseDnsResolver(new DemoNetworkMap(), DemoTimings.Instant);

        Assert.Null(await reverse.ResolveAsync("10.50.3.250", CancellationToken.None));
    }

    [Fact]
    public async Task Icmp_probe_matches_demo_online_status()
    {
        var data = TestSupport.Data();
        var icmp = new DemoIcmpProbe(data, new DemoNetworkMap(), DemoTimings.Instant);
        var online = TestSupport.OnlineWorkstation(data);
        var offline = TestSupport.Offline(data);

        Assert.True(await icmp.PingAsync(online.HostName, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await icmp.PingAsync(offline.HostName, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task Subnet_scanner_finds_devices_matching_demo_computers_and_some_outside_ad()
    {
        var data = TestSupport.Data();
        var map = new DemoNetworkMap();
        var dns = new DemoDnsResolverService(data, map, DemoTimings.Instant);
        var online = TestSupport.OnlineWorkstation(data);
        await dns.ResolveAsync(online.HostName, CancellationToken.None); // регистрирует IP в map, как это сделал бы DNS-резолв компьютера

        var scanner = new DemoSubnetScanner(data, map, TimeProvider.System, DemoTimings.Instant);
        var subnet = new SubnetDefinition { Name = "Test", Cidr = DemoNetworkMap.OfficeCidr, ProbeKind = ProbeKind.Both };

        var found = await scanner.ScanAsync(subnet, null, CancellationToken.None).ToListAsync();

        Assert.Contains(found, d => d.ReverseDnsName == online.HostName);
        Assert.Contains(found, d => d.ReverseDnsName is null); // устройство вне AD
    }

    [Fact]
    public async Task Subnet_scan_survives_a_computer_removed_after_its_ip_was_assigned()
    {
        var data = TestSupport.Data();
        var map = new DemoNetworkMap();
        var dns = new DemoDnsResolverService(data, map, DemoTimings.Instant);
        var online = TestSupport.OnlineWorkstation(data);
        var ip = (await dns.ResolveAsync(online.HostName, CancellationToken.None)).Single();
        Assert.True(new DemoComputerDirectory(data, DemoTimings.Instant).Remove(online.Id));

        var scanner = new DemoSubnetScanner(data, map, TimeProvider.System, DemoTimings.Instant);
        var subnet = new SubnetDefinition { Name = "Test", Cidr = ip + "/32", ProbeKind = ProbeKind.Both };

        var found = await scanner.ScanAsync(subnet, null, CancellationToken.None).ToListAsync();

        Assert.Empty(found); // компьютера больше нет — его адрес не отвечает
    }

    [Fact]
    public void Concurrent_requests_for_one_host_get_one_address()
    {
        var map = new DemoNetworkMap();

        var ips = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 64, _ => ips.Add(map.IpFor("PC-RACE.corp.local")));

        var ip = Assert.Single(ips.Distinct());
        Assert.Equal("PC-RACE.corp.local", map.HostFor(ip));
        Assert.Equal(ip, map.IpFor("pc-race.corp.local"));
    }

    [Fact]
    public async Task Subnet_scan_results_are_deterministic_for_the_same_subnet()
    {
        var data = TestSupport.Data();
        var map = new DemoNetworkMap();
        var scanner = new DemoSubnetScanner(data, map, TimeProvider.System, DemoTimings.Instant);
        var subnet = new SubnetDefinition { Name = "Test", Cidr = "10.50.0.0/24", ProbeKind = ProbeKind.Both };

        var first = (await scanner.ScanAsync(subnet, null, CancellationToken.None).ToListAsync()).Select(d => d.IpAddress).OrderBy(x => x).ToList();
        var second = (await scanner.ScanAsync(subnet, null, CancellationToken.None).ToListAsync()).Select(d => d.IpAddress).OrderBy(x => x).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Subnet_scan_reports_progress_up_to_total()
    {
        var data = TestSupport.Data();
        var scanner = new DemoSubnetScanner(data, new DemoNetworkMap(), TimeProvider.System, DemoTimings.Instant);
        var subnet = new SubnetDefinition { Name = "Test", Cidr = "10.50.8.0/29", ProbeKind = ProbeKind.Both }; // 6 usable
        var reports = new List<ScanProgress>();

        await foreach (var _ in scanner.ScanAsync(subnet, new Progress(reports), CancellationToken.None))
        {
        }

        Assert.Equal(6, reports[^1].Total);
        Assert.Equal(6, reports[^1].Completed);
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        var data = TestSupport.Data();
        var scanner = new DemoSubnetScanner(data, new DemoNetworkMap(), TimeProvider.System, DemoTimings.Default);
        var subnet = new SubnetDefinition { Name = "Test", Cidr = "10.50.0.0/20", ProbeKind = ProbeKind.Both };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in scanner.ScanAsync(subnet, null, cts.Token))
            {
            }
        });
    }

    [Fact]
    public void Seeding_fills_favorites_groups_and_subnets_only_when_empty()
    {
        var data = TestSupport.Data();
        var settings = new AppSettings();

        DemoSettingsSeeding.SeedIfEmpty(settings, data);

        Assert.NotEmpty(settings.FavoriteComputers);
        Assert.NotEmpty(settings.ComputerGroups);
        Assert.NotEmpty(settings.Subnets);

        // Пользователь уже настроил что-то своё (одно избранное) — повторный вызов не должен его стереть/добавить ещё.
        var customFavorite = data.GetComputers().Last().Id;
        settings.FavoriteComputers.Clear();
        settings.FavoriteComputers.Add(customFavorite);
        DemoSettingsSeeding.SeedIfEmpty(settings, data);

        Assert.Equal(new[] { customFavorite }, settings.FavoriteComputers);
    }

    [Fact]
    public void Seeding_does_not_refill_after_user_deleted_everything()
    {
        var data = TestSupport.Data();
        var settings = new AppSettings();
        DemoSettingsSeeding.SeedIfEmpty(settings, data);
        settings.FavoriteComputers.Clear();
        settings.ComputerGroups.Clear();
        settings.Subnets.Clear();

        DemoSettingsSeeding.SeedIfEmpty(settings, data);

        Assert.Empty(settings.FavoriteComputers);
        Assert.Empty(settings.ComputerGroups);
        Assert.Empty(settings.Subnets);
    }

    private sealed class Progress(List<ScanProgress> list) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => list.Add(value);
    }
}
