using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Core.Tests;

public class ComputersFilterTests
{
    private static ComputerInfo Make(
        string name, bool enabled = true, AvailabilityStatus? status = null, bool? rdp = null, bool? smb = null, bool? ssh = null,
        string? ip = null, string ou = "OU=Computers,DC=corp,DC=local", string? description = null, DateTime? lastChecked = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DistinguishedName = $"CN={name},{ou}",
            Enabled = enabled,
            Description = description,
            IpAddresses = ip is null ? null : new[] { ip },
            RdpAvailable = rdp,
            SmbAvailable = smb,
            SshAvailable = ssh,
            LastCheckedUtc = lastChecked,
            IsChecking = status == AvailabilityStatus.Checking,
            IsOnline = status switch { AvailabilityStatus.Online => true, AvailabilityStatus.Offline => false, _ => null },
        };

    [Fact]
    public void Default_filter_returns_everything_enabled()
    {
        var computers = new[] { Make("A"), Make("B", enabled: false) };

        var result = ComputersFilter.Apply(computers, new ComputersFilterState()).ToList();

        Assert.Single(result);
        Assert.Equal("A", result[0].Name);
    }

    [Fact]
    public void Show_disabled_includes_disabled_computers()
    {
        var computers = new[] { Make("A"), Make("B", enabled: false) };
        var filter = new ComputersFilterState { ShowDisabledAd = true };

        var result = ComputersFilter.Apply(computers, filter).ToList();

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData(AvailabilityFilter.Online, "A")]
    [InlineData(AvailabilityFilter.Offline, "B")]
    [InlineData(AvailabilityFilter.Unknown, "C")]
    public void Availability_filter_selects_the_right_bucket(AvailabilityFilter availability, string expectedName)
    {
        var computers = new[] { Make("A", status: AvailabilityStatus.Online), Make("B", status: AvailabilityStatus.Offline), Make("C") };
        var filter = new ComputersFilterState { Availability = availability, ShowDisabledAd = true };

        var result = ComputersFilter.Apply(computers, filter).ToList();

        Assert.Equal(expectedName, Assert.Single(result).Name);
    }

    [Fact]
    public void Unknown_filter_also_matches_checking_status()
    {
        var checking = Make("CHK", status: AvailabilityStatus.Checking);
        var filter = new ComputersFilterState { Availability = AvailabilityFilter.Unknown };

        var result = ComputersFilter.Apply(new[] { checking }, filter).ToList();

        Assert.Single(result);
    }

    [Fact]
    public void Protocol_filters_combine_with_and()
    {
        var computers = new[]
        {
            Make("RDP-ONLY", rdp: true, smb: false),
            Make("BOTH", rdp: true, smb: true),
            Make("SMB-ONLY", rdp: false, smb: true),
        };
        var filter = new ComputersFilterState { RdpFilter = true, SmbFilter = true };

        var result = ComputersFilter.Apply(computers, filter).ToList();

        Assert.Equal("BOTH", Assert.Single(result).Name);
    }

    [Fact]
    public void Text_filters_combine_with_and()
    {
        var computers = new[]
        {
            Make("PC-ACC-01", description: "Бухгалтерия"),
            Make("PC-ACC-02", description: "Отдел кадров"),
            Make("PC-SALES-01", description: "Бухгалтерия"),
        };
        var filter = new ComputersFilterState { NameFilter = "ACC", DescriptionFilter = "Бухгалтерия" };

        var result = ComputersFilter.Apply(computers, filter).ToList();

        Assert.Equal("PC-ACC-01", Assert.Single(result).Name);
    }

    [Fact]
    public void Ip_filter_matches_substring_of_any_address()
    {
        var computers = new[] { Make("A", ip: "10.0.0.5"), Make("B", ip: "10.0.0.6") };
        var filter = new ComputersFilterState { IpFilter = "0.5" };

        var result = ComputersFilter.Apply(computers, filter).ToList();

        Assert.Equal("A", Assert.Single(result).Name);
    }

    [Fact]
    public void Ou_filter_matches_parent_dn()
    {
        var computers = new[]
        {
            Make("A", ou: "OU=Finance,DC=corp,DC=local"),
            Make("B", ou: "OU=Sales,DC=corp,DC=local"),
        };
        var filter = new ComputersFilterState { OuFilter = "Finance" };

        var result = ComputersFilter.Apply(computers, filter).ToList();

        Assert.Equal("A", Assert.Single(result).Name);
    }

    [Fact]
    public void Source_filter_distinguishes_ad_from_scan()
    {
        var ad = Make("AD-1");
        var scan = Make("SCAN-1");

        var onlyAd = ComputersFilter.Apply(new[] { ad }, new ComputersFilterState { Source = ComputerSourceFilter.Ad }, isFromScan: false).ToList();
        var onlyScan = ComputersFilter.Apply(new[] { scan }, new ComputersFilterState { Source = ComputerSourceFilter.Ad }, isFromScan: true).ToList();

        Assert.Single(onlyAd);
        Assert.Empty(onlyScan);
    }

    [Fact]
    public void Default_sort_puts_online_first_then_checking_unknown_offline()
    {
        var computers = new[]
        {
            Make("OFFLINE", status: AvailabilityStatus.Offline),
            Make("ONLINE", status: AvailabilityStatus.Online),
            Make("UNKNOWN"),
            Make("CHECKING", status: AvailabilityStatus.Checking),
        };

        var sorted = ComputersFilter.Sort(computers, new ComputersFilterState()).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "ONLINE", "CHECKING", "UNKNOWN", "OFFLINE" }, sorted);
    }

    [Fact]
    public void Sort_by_name_is_alphabetical_and_descending_reverses_it()
    {
        var computers = new[] { Make("B"), Make("A"), Make("C") };
        var filter = new ComputersFilterState { SortBy = ComputersSortBy.Name };

        var ascending = ComputersFilter.Sort(computers, filter).Select(c => c.Name);
        var descending = ComputersFilter.Sort(computers, new ComputersFilterState { SortBy = ComputersSortBy.Name, SortDescending = true }).Select(c => c.Name);

        Assert.Equal(new[] { "A", "B", "C" }, ascending);
        Assert.Equal(new[] { "C", "B", "A" }, descending);
    }

    [Fact]
    public void Sort_by_ip_address_orders_numerically_and_puts_computers_without_ip_last()
    {
        var computers = new[] { Make("A", ip: "10.0.0.20"), Make("B", ip: "10.0.0.5"), Make("C", ip: null) };
        var filter = new ComputersFilterState { SortBy = ComputersSortBy.IpAddress };

        var sorted = ComputersFilter.Sort(computers, filter).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "B", "A", "C" }, sorted); // 10.0.0.5 < 10.0.0.20, без адреса — в конце
    }

    [Fact]
    public void Sort_by_last_checked_orders_oldest_first_unchecked_last_is_not_guaranteed_but_treated_as_min()
    {
        var old = Make("OLD", lastChecked: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Make("NEW", lastChecked: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var never = Make("NEVER");
        var filter = new ComputersFilterState { SortBy = ComputersSortBy.LastChecked };

        var sorted = ComputersFilter.Sort(new[] { newer, old, never }, filter).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "NEVER", "OLD", "NEW" }, sorted);
    }

    // ----- smoke-тест на нескольких тысячах компьютеров -------------------------------------------------------

    [Fact]
    public void Filters_and_sorts_several_thousand_computers_correctly_and_quickly()
    {
        var random = new Random(42);
        var computers = Enumerable.Range(0, 6000).Select(i =>
        {
            var status = (i % 4) switch
            {
                0 => AvailabilityStatus.Online,
                1 => AvailabilityStatus.Offline,
                2 => AvailabilityStatus.Checking,
                _ => (AvailabilityStatus?)null,
            };
            return Make(
                $"PC-{i:0000}",
                enabled: i % 10 != 0,
                status: status,
                rdp: i % 3 == 0,
                ip: $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}");
        }).OrderBy(_ => random.Next()).ToList();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = ComputersFilter.ApplyAndSort(computers, new ComputersFilterState { Availability = AvailabilityFilter.Online, RdpFilter = true });
        watch.Stop();

        var expectedCount = computers.Count(c => c.Enabled && c.AvailabilityStatus == AvailabilityStatus.Online && c.RdpAvailable == true);
        Assert.Equal(expectedCount, result.Count);
        Assert.All(result, c => Assert.Equal(AvailabilityStatus.Online, c.AvailabilityStatus));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Фильтрация 6000 компьютеров заняла {watch.Elapsed.TotalSeconds:0.00} с — это не микробенчмарк, но так долго быть не должно.");

        // Сортировка по умолчанию держит порядок: доступные блоки идут так, как определено рангом.
        var full = ComputersFilter.ApplyAndSort(computers, new ComputersFilterState { ShowDisabledAd = true });
        Assert.Equal(computers.Count, full.Count);
        var ranks = full.Select(c => c.AvailabilityStatus).ToList();
        Assert.True(ranks.SequenceEqual(ranks.OrderBy(AvailabilityOrderForAssert)), "компьютеры должны идти блоками по статусу доступности");
    }

    private static int AvailabilityOrderForAssert(AvailabilityStatus status) => status switch
    {
        AvailabilityStatus.Online => 0,
        AvailabilityStatus.Checking => 1,
        AvailabilityStatus.Unknown => 2,
        _ => 3,
    };
}
