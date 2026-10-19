using RemoteShadow.Core.Util;

namespace RemoteShadow.Core.Tests;

/// <summary>
/// Самые ценные тесты этой задачи: расчёт диапазонов CIDR. Любая ошибка здесь приводит либо к пропуску адресов
/// при сканировании подсети, либо к сканированию чужих адресов.
/// </summary>
public class CidrTests
{
    [Theory]
    [InlineData("10.0.0.0/24", "10.0.0.0", "10.0.0.255", 256, "10.0.0.1", "10.0.0.254", 254)]
    [InlineData("192.168.1.0/25", "192.168.1.0", "192.168.1.127", 128, "192.168.1.1", "192.168.1.126", 126)]
    [InlineData("10.4.0.0/22", "10.4.0.0", "10.4.3.255", 1024, "10.4.0.1", "10.4.3.254", 1022)]
    public void Parses_common_prefixes_and_excludes_network_and_broadcast(
        string cidr, string network, string broadcast, long total, string firstUsable, string lastUsable, long usable)
    {
        var range = Cidr.Parse(cidr);

        Assert.Equal(network, range.NetworkAddress.ToString());
        Assert.Equal(broadcast, range.BroadcastAddress.ToString());
        Assert.Equal(total, range.TotalAddresses);
        Assert.Equal(firstUsable, range.FirstUsable!.ToString());
        Assert.Equal(lastUsable, range.LastUsable!.ToString());
        Assert.Equal(usable, range.UsableCount);
        Assert.Equal(total, range.AllAddresses().Count());
        Assert.Equal(usable, range.UsableAddresses().Count());
    }

    [Fact]
    public void Slash_31_is_point_to_point_both_addresses_usable_without_exclusion()
    {
        var range = Cidr.Parse("10.0.0.4/31");

        Assert.Equal("10.0.0.4", range.NetworkAddress.ToString());
        Assert.Equal("10.0.0.5", range.BroadcastAddress.ToString());
        Assert.Equal(2, range.TotalAddresses);
        Assert.Equal(2, range.UsableCount);
        Assert.Equal("10.0.0.4", range.FirstUsable!.ToString());
        Assert.Equal("10.0.0.5", range.LastUsable!.ToString());
        Assert.Equal(new[] { "10.0.0.4", "10.0.0.5" }, range.UsableAddresses().Select(a => a.ToString()));
    }

    [Fact]
    public void Slash_32_is_a_single_usable_address()
    {
        var range = Cidr.Parse("10.0.0.7/32");

        Assert.Equal("10.0.0.7", range.NetworkAddress.ToString());
        Assert.Equal("10.0.0.7", range.BroadcastAddress.ToString());
        Assert.Equal(1, range.TotalAddresses);
        Assert.Equal(1, range.UsableCount);
        Assert.Equal("10.0.0.7", range.FirstUsable!.ToString());
        Assert.Equal("10.0.0.7", range.LastUsable!.ToString());
        Assert.Equal(new[] { "10.0.0.7" }, range.AllAddresses().Select(a => a.ToString()));
    }

    [Fact]
    public void Slash_30_excludes_network_and_broadcast_leaving_two_usable()
    {
        var range = Cidr.Parse("10.0.0.0/30");

        Assert.Equal(4, range.TotalAddresses);
        Assert.Equal(2, range.UsableCount);
        Assert.Equal("10.0.0.1", range.FirstUsable!.ToString());
        Assert.Equal("10.0.0.2", range.LastUsable!.ToString());
    }

    [Fact]
    public void Address_is_masked_down_to_the_network_address()
    {
        var range = Cidr.Parse("10.0.1.200/24");

        Assert.Equal("10.0.1.0", range.NetworkAddress.ToString());
        Assert.Equal("10.0.1.255", range.BroadcastAddress.ToString());
    }

    [Fact]
    public void Contains_checks_membership_in_the_range()
    {
        var range = Cidr.Parse("10.0.0.0/24");

        Assert.True(range.Contains(System.Net.IPAddress.Parse("10.0.0.1")));
        Assert.True(range.Contains(System.Net.IPAddress.Parse("10.0.0.255")));
        Assert.False(range.Contains(System.Net.IPAddress.Parse("10.0.1.0")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/abc")]
    [InlineData("300.0.0.0/24")]
    [InlineData("10.0.0/24")]
    [InlineData("10.0.0.0.1/24")]
    [InlineData("::1/64")]
    [InlineData("not-an-ip/24")]
    [InlineData("10.0.0.0/24/8")]
    public void Rejects_invalid_input(string? cidr)
    {
        Assert.False(Cidr.TryParse(cidr, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<FormatException>(() => Cidr.Parse(cidr));
    }

    [Fact]
    public void Slash_0_covers_the_entire_ipv4_space_without_overflow()
    {
        var range = Cidr.Parse("1.2.3.4/0");

        Assert.Equal("0.0.0.0", range.NetworkAddress.ToString());
        Assert.Equal("255.255.255.255", range.BroadcastAddress.ToString());
        Assert.Equal(4_294_967_296L, range.TotalAddresses);
    }
}
