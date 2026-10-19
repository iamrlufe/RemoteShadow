using System.Globalization;
using System.Windows.Data;
using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Pages;

/// <summary>Значения перечислений по-русски: фильтры «Компьютеров» (выпадающие списки) и статус проверки диагностики.</summary>
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
        DiagnosticStatus status => DiagnosticsPageViewModel.StatusLabel(status),
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Статус проверки диагностики → значок (глиф Segoe Fluent Icons): галочка, треугольник, крестик, «i».</summary>
[ValueConversion(typeof(DiagnosticStatus), typeof(string))]
public sealed class DiagnosticStatusIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => RemoteShadow.App.Shell.IconMap.Get(value switch
    {
        DiagnosticStatus.Ok => "circle-check",
        DiagnosticStatus.Warning => "alert-triangle-filled",
        DiagnosticStatus.Fail => "circle-x",
        _ => "info-circle",
    });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
