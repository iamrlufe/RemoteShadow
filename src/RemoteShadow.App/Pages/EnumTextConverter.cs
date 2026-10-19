using System.Globalization;
using System.Windows.Data;
using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Pages;

/// <summary>Значения перечислений по-русски: фильтры «Компьютеров» (выпадающие списки).</summary>
[ValueConversion(typeof(Enum), typeof(string))]
public sealed class EnumTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AvailabilityFilter.All => "Все",
        AvailabilityFilter.Online => "В сети",
        AvailabilityFilter.Offline => "Не в сети",
        AvailabilityFilter.Unknown => "Не проверялись",
        ComputerSourceFilter.All => "Все источники",
        ComputerSourceFilter.Ad => "Из AD",
        ComputerSourceFilter.Scan => "Найденные сканированием",
        ComputersSortBy.Availability => "Сначала доступные",
        ComputersSortBy.Name => "По имени",
        ComputersSortBy.IpAddress => "По IP-адресу",
        ComputersSortBy.LastChecked => "По времени проверки",
        ProbeKind.TcpOnly => "Только TCP",
        ProbeKind.IcmpOnly => "Только ping",
        ProbeKind.Both => "Ping и TCP",
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
