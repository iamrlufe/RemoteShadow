using System.Text.Json;
using RemoteShadow.Core.Models;
using RemoteShadow.Infrastructure;

namespace RemoteShadow.Infrastructure.Tests;

public class DiscoveredDevicePersistenceTests
{
    [Fact]
    public void Discovered_devices_survive_a_settings_round_trip_with_all_collected_info()
    {
        var subnetId = Guid.NewGuid();
        var settings = new AppSettings
        {
            DiscoveredDevices =
            {
                new DiscoveredDevice
                {
                    Key = "10.0.0.196", SubnetId = subnetId, IpAddress = "10.0.0.196", IsOnline = true, FoundInLastScan = true,
                    Hostname = "WS-196", NameSource = "NetBIOS", Mac = "00:1A:2B:3C:4D:5E", Vendor = "Dell Inc.", DeviceKind = "Windows",
                    OsName = "Microsoft Windows 11 Pro", Model = "OptiPlex 7010", SerialNumber = "ABC123",
                    Details = new Dictionary<string, string> { ["Память"] = "32 ГБ" },
                    EnrichedUtc = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc),
                },
            },
        };

        var json = JsonSerializer.Serialize(settings, JsonSettingsStore.JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, JsonSettingsStore.JsonOptions)!;

        var d = Assert.Single(back.DiscoveredDevices);
        Assert.Equal("WS-196", d.Hostname);
        Assert.Equal("Dell Inc.", d.Vendor);
        Assert.Equal("32 ГБ", d.Details!["Память"]);
        Assert.Equal(subnetId, d.SubnetId);
        Assert.NotNull(d.EnrichedUtc);
        Assert.DoesNotContain("DisplayName", json);
    }

    [Fact]
    public void Display_name_falls_back_from_hostname_to_ip_and_summary_lists_known_facts()
    {
        var unnamed = new DiscoveredDevice { Key = "10.0.0.9", SubnetId = Guid.Empty, IpAddress = "10.0.0.9" };
        Assert.Equal("10.0.0.9", unnamed.DisplayName);
        Assert.Equal(string.Empty, unnamed.Summary);

        var named = unnamed with { Hostname = "PRN-9", DeviceKind = "Принтер", Vendor = "HP Inc.", Mac = "3C:D9:2B:00:00:01" };
        Assert.Equal("PRN-9", named.DisplayName);
        Assert.Equal("10.0.0.9 · Принтер · HP Inc. · 3C:D9:2B:00:00:01", named.Summary);
    }
}
