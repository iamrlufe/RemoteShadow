using RemoteShadow.Core.Models;

namespace RemoteShadow.Core.Util;

/// <summary>
/// Фильтрация и сортировка списка компьютеров для раздела «Компьютеры» — общая логика для ViewModel и тестов
/// (так фильтры/сортировка проверяются без WPF и без реального индекса).
/// </summary>
public static class ComputersFilter
{
    /// <summary>Применяет текстовые фильтры (через И), фильтры по доступности/протоколам/источнику и «показывать отключённые».</summary>
    public static IEnumerable<ComputerInfo> Apply(IEnumerable<ComputerInfo> computers, ComputersFilterState filter, bool isFromScan = false)
    {
        ArgumentNullException.ThrowIfNull(computers);
        ArgumentNullException.ThrowIfNull(filter);

        IEnumerable<ComputerInfo> query = computers;

        if (!filter.ShowDisabledAd) query = query.Where(c => c.Enabled);

        if (filter.Source != ComputerSourceFilter.All)
        {
            var wantScan = filter.Source == ComputerSourceFilter.Scan;
            query = query.Where(_ => isFromScan == wantScan);
        }

        query = filter.Availability switch
        {
            AvailabilityFilter.Online => query.Where(c => c.AvailabilityStatus == AvailabilityStatus.Online),
            AvailabilityFilter.Offline => query.Where(c => c.AvailabilityStatus == AvailabilityStatus.Offline),
            AvailabilityFilter.Unknown => query.Where(c => c.AvailabilityStatus is AvailabilityStatus.Unknown or AvailabilityStatus.Checking),
            _ => query,
        };

        if (filter.RdpFilter is { } rdp) query = query.Where(c => c.RdpAvailable == rdp);
        if (filter.SmbFilter is { } smb) query = query.Where(c => c.SmbAvailable == smb);
        if (filter.SshFilter is { } ssh) query = query.Where(c => c.SshAvailable == ssh);

        if (!string.IsNullOrWhiteSpace(filter.NameFilter))
            query = query.Where(c => c.Name.Contains(filter.NameFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(filter.DescriptionFilter))
            query = query.Where(c => c.Description?.Contains(filter.DescriptionFilter, StringComparison.OrdinalIgnoreCase) == true);

        if (!string.IsNullOrWhiteSpace(filter.OuFilter))
            query = query.Where(c => c.ParentDn.Contains(filter.OuFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(filter.IpFilter))
        {
            query = query.Where(c => c.IpAddresses?.Any(ip => ip.Contains(filter.IpFilter, StringComparison.OrdinalIgnoreCase)) == true);
        }

        return query;
    }

    /// <summary>Сортирует по <see cref="ComputersFilterState.SortBy"/>: по умолчанию доступные идут первыми.</summary>
    public static IEnumerable<ComputerInfo> Sort(IEnumerable<ComputerInfo> computers, ComputersFilterState filter)
    {
        ArgumentNullException.ThrowIfNull(computers);
        ArgumentNullException.ThrowIfNull(filter);

        IOrderedEnumerable<ComputerInfo> ordered = filter.SortBy switch
        {
            ComputersSortBy.Name => computers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
            ComputersSortBy.IpAddress => computers.OrderBy(c => IpSortKey(c.IpAddresses?.FirstOrDefault())),
            ComputersSortBy.LastChecked => computers.OrderBy(c => c.LastCheckedUtc ?? DateTime.MinValue),
            _ => computers.OrderBy(AvailabilityRank),
        };

        // Вторичная сортировка по имени — стабильный порядок при равенстве основного ключа.
        var withTieBreak = filter.SortBy == ComputersSortBy.Name
            ? ordered
            : ordered.ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        return filter.SortDescending ? withTieBreak.Reverse() : withTieBreak;
    }

    /// <summary>Числовой ключ IPv4 (10.50.0.9 раньше 10.50.0.100); без адреса или не IPv4 — в конец.</summary>
    private static long IpSortKey(string? address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return long.MaxValue;

        var bytes = ip.GetAddressBytes();
        return ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
    }

    /// <summary>Доступные компьютеры наверх по умолчанию: Online &lt; Checking &lt; Unknown &lt; Offline.</summary>
    private static int AvailabilityRank(ComputerInfo computer) => computer.AvailabilityStatus switch
    {
        AvailabilityStatus.Online => 0,
        AvailabilityStatus.Checking => 1,
        AvailabilityStatus.Unknown => 2,
        _ => 3,
    };

    /// <summary>Фильтрует и сортирует за один проход.</summary>
    public static IReadOnlyList<ComputerInfo> ApplyAndSort(IEnumerable<ComputerInfo> computers, ComputersFilterState filter, bool isFromScan = false) =>
        Sort(Apply(computers, filter, isFromScan), filter).ToList();
}
