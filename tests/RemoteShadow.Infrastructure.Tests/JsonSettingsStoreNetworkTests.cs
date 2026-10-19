using RemoteShadow.Core.Models;

namespace RemoteShadow.Infrastructure.Tests;

/// <summary>
/// Сохранение/чтение настроек: группы компьютеров, подсети и фильтры раздела «Компьютеры», а также обратная
/// совместимость со старым форматом файла (без новых полей).
/// </summary>
public class JsonSettingsStoreNetworkTests
{
    [Fact]
    public void Saves_and_loads_groups_subnets_and_filters_round_trip()
    {
        using var temp = new TempDir();
        var store = new JsonSettingsStore(temp.Paths);
        var computerId = Guid.NewGuid();
        var settings = new AppSettings
        {
            FavoriteComputers = { computerId },
            ComputerGroups =
            {
                new ComputerGroup { Name = "Бухгалтерия", ComputerIds = { computerId }, DiscoveredDeviceKeys = { "10.0.0.5" } },
            },
            Subnets =
            {
                new SubnetDefinition { Name = "Офис", Cidr = "10.0.0.0/24", ProbeKind = ProbeKind.Both, TimeoutMs = 300, Parallelism = 16 },
            },
            ComputersFilter = new ComputersFilterState
            {
                Availability = AvailabilityFilter.Online,
                Source = ComputerSourceFilter.Scan,
                NameFilter = "pc-",
                RdpFilter = true,
                SortBy = ComputersSortBy.IpAddress,
                SortDescending = true,
            },
        };

        store.Save(settings);
        var reloaded = new JsonSettingsStore(temp.Paths).Load();

        Assert.Equal(computerId, Assert.Single(reloaded.FavoriteComputers));
        var group = Assert.Single(reloaded.ComputerGroups);
        Assert.Equal("Бухгалтерия", group.Name);
        Assert.Equal(computerId, Assert.Single(group.ComputerIds));
        Assert.Equal("10.0.0.5", Assert.Single(group.DiscoveredDeviceKeys));

        var subnet = Assert.Single(reloaded.Subnets);
        Assert.Equal("10.0.0.0/24", subnet.Cidr);
        Assert.Equal(ProbeKind.Both, subnet.ProbeKind);
        Assert.Equal(300, subnet.TimeoutMs);
        Assert.Equal(16, subnet.Parallelism);

        Assert.Equal(AvailabilityFilter.Online, reloaded.ComputersFilter.Availability);
        Assert.Equal(ComputerSourceFilter.Scan, reloaded.ComputersFilter.Source);
        Assert.Equal("pc-", reloaded.ComputersFilter.NameFilter);
        Assert.True(reloaded.ComputersFilter.RdpFilter);
        Assert.Equal(ComputersSortBy.IpAddress, reloaded.ComputersFilter.SortBy);
        Assert.True(reloaded.ComputersFilter.SortDescending);
    }

    [Fact]
    public void Old_format_file_without_new_fields_loads_with_defaults()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        // Файл из версии до этой задачи: только самые старые поля, без групп/подсетей/фильтров/портов.
        File.WriteAllText(temp.Paths.SettingsFile, """
            {
              "useCurrentUser": true,
              "lastDomain": "corp.local",
              "favoriteComputers": ["11111111-1111-1111-1111-111111111111"]
            }
            """);

        var settings = new JsonSettingsStore(temp.Paths).Load();

        Assert.Equal("corp.local", settings.LastDomain);
        Assert.Single(settings.FavoriteComputers);
        Assert.NotNull(settings.ComputerGroups);
        Assert.Empty(settings.ComputerGroups);
        Assert.NotNull(settings.Subnets);
        Assert.Empty(settings.Subnets);
        Assert.NotNull(settings.ComputersFilter);
        Assert.Equal(AvailabilityFilter.All, settings.ComputersFilter.Availability);

        // Новые настройки доступности приходят со значениями по умолчанию, а не падают с ошибкой.
        Assert.Equal(3389, settings.RdpPort);
        Assert.Equal(445, settings.SmbPort);
        Assert.Equal(22, settings.SshPort);
        Assert.True(settings.CheckIcmp);
        Assert.True(settings.CheckRdp);
        Assert.True(settings.CheckSmb);
        Assert.False(settings.CheckSsh);
    }

    [Fact]
    public void Null_lists_in_a_hand_edited_file_do_not_crash_and_become_empty()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Paths.DataDir);
        File.WriteAllText(temp.Paths.SettingsFile, """{ "computerGroups": null, "subnets": null, "computersFilter": null }""");

        var settings = new JsonSettingsStore(temp.Paths).Load();

        Assert.NotNull(settings.ComputerGroups);
        Assert.NotNull(settings.Subnets);
        Assert.NotNull(settings.ComputersFilter);
    }

    [Fact]
    public void Removing_a_group_does_not_touch_favorite_computers_or_anything_else()
    {
        using var temp = new TempDir();
        var store = new JsonSettingsStore(temp.Paths);
        var computerId = Guid.NewGuid();
        var settings = new AppSettings
        {
            FavoriteComputers = { computerId },
            ComputerGroups = { new ComputerGroup { Name = "Temp", ComputerIds = { computerId } } },
        };
        store.Save(settings);

        var loaded = store.Load();
        loaded.ComputerGroups.Clear(); // как сделал бы UI после удаления группы — favorites не трогаем
        store.Save(loaded);

        var reloaded = new JsonSettingsStore(temp.Paths).Load();
        Assert.Empty(reloaded.ComputerGroups);
        Assert.Equal(computerId, Assert.Single(reloaded.FavoriteComputers));
    }
}
