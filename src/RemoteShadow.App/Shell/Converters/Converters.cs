using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Shell.Converters;

/// <summary>Разбор <c>ConverterParameter</c>: слова через запятую или пробел, например «invert,hidden».</summary>
internal static class ConverterParameters
{
    public static bool Has(object? parameter, string word) =>
        parameter is string text && text.Contains(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// bool → Visibility. Параметры: <c>invert</c> — наоборот; <c>hidden</c> — скрывать через Hidden вместо Collapsed.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (ConverterParameters.Has(parameter, "invert")) visible = !visible;
        return visible ? Visibility.Visible : ConverterParameters.Has(parameter, "hidden") ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        return ConverterParameters.Has(parameter, "invert") ? !visible : visible;
    }
}

/// <summary>bool → !bool. null считается false.</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// null или пустая строка → Collapsed, иначе Visible. Параметр <c>invert</c> меняет наоборот
/// (показать, когда значения нет).
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && !(value is string text && string.IsNullOrWhiteSpace(text));
        if (ConverterParameters.Has(parameter, "invert")) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Число элементов (int, коллекция) → Visible, когда их нет: для «пустого состояния» списка.
/// Параметр <c>invert</c> — Visible, когда элементы есть.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            int count => count == 0,
            string text => text.Length == 0,
            ICollection collection => collection.Count == 0,
            IEnumerable sequence => !sequence.Cast<object?>().Any(),
            _ => false,
        };

        if (ConverterParameters.Has(parameter, "invert")) empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Имя значка (Tabler) → глиф Segoe Fluent Icons. Для <c>TextBlock</c> со стилем <c>IconTextStyle</c>.</summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class IconGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        IconMap.Get(value as string ?? parameter as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>DateTime (UTC) → «2 мин назад». null → пустая строка.</summary>
[ValueConversion(typeof(DateTime), typeof(string))]
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTime moment => RelativeTimeFormatter.Format(RelativeTimeFormatter.ToUtc(moment), DateTime.UtcNow),
        DateTimeOffset moment => RelativeTimeFormatter.Format(moment.UtcDateTime, DateTime.UtcNow),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Статус (bool?, DiagnosticStatus, SessionState, ActionStatus, HostQueryStatus или слово «success», «warning»,
/// «error», «info») → кисть темы Fluent. Значение вычисляется при привязке: при смене темы на лету
/// элемент нужно привязать заново, поэтому для точки и бейджа лучше стили <c>StatusDotStyle</c> и <c>BadgeStyle</c>.
/// </summary>
[ValueConversion(typeof(object), typeof(Brush))]
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ToneResources.GetBrush(StatusToneMap.From(value));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Статус → стиль бейджа (<c>BadgeSuccessStyle</c>, <c>BadgeWarningStyle</c> …). Применяется к <c>Border.Style</c>.</summary>
[ValueConversion(typeof(object), typeof(Style))]
public sealed class StatusToBadgeStyleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ToneResources.GetBadgeStyle(StatusToneMap.From(value));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool? → «online» / «offline» / «unknown» — значение <c>Tag</c> для стиля <c>StatusDotStyle</c>.</summary>
[ValueConversion(typeof(bool?), typeof(string))]
public sealed class OnlineStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => "online",
        false => "offline",
        _ => "unknown",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>SessionState → «активна», «отключена» …</summary>
[ValueConversion(typeof(SessionState), typeof(string))]
public sealed class SessionStateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionState state ? SessionStateText.Get(state) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// <see cref="AvailabilityStatus"/> → «online»/«offline»/«checking»/«unknown» — значение <c>Tag</c> для
/// <c>StatusDotStyle</c> (зелёный/красный/жёлтый/серый).
/// </summary>
[ValueConversion(typeof(AvailabilityStatus), typeof(string))]
public sealed class AvailabilityStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AvailabilityStatus.Online => "online",
        AvailabilityStatus.Offline => "offline",
        AvailabilityStatus.Checking => "checking",
        _ => "unknown",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool? → «есть» / «нет» / «?» — для колонок RDP/SMB/SSH в таблице компьютеров.</summary>
[ValueConversion(typeof(bool?), typeof(string))]
public sealed class ProtocolAvailabilityTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => "✓",
        false => "✕",
        _ => "?",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool (избранное выбранного компьютера) → подпись кнопки-переключателя.</summary>
[ValueConversion(typeof(bool), typeof(string))]
public sealed class FavoriteButtonTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "★ Убрать из избранного" : "☆ В избранное";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Кисти и стили по <see cref="StatusTone"/>: из ресурсов приложения, а если их нет — запасные цвета.</summary>
internal static class ToneResources
{
    private static readonly Dictionary<StatusTone, Brush> FallbackBrushes = new()
    {
        [StatusTone.Success] = Freeze(0x10, 0x7C, 0x10),
        [StatusTone.Warning] = Freeze(0x9D, 0x5D, 0x00),
        [StatusTone.Critical] = Freeze(0xC4, 0x2B, 0x1C),
        [StatusTone.Info] = Freeze(0x00, 0x78, 0xD4),
        [StatusTone.Neutral] = Freeze(0x60, 0x5E, 0x5C),
        [StatusTone.Muted] = Freeze(0x8A, 0x88, 0x86),
    };

    public static string BrushKey(StatusTone tone) => tone switch
    {
        StatusTone.Success => "SystemFillColorSuccessBrush",
        StatusTone.Warning => "SystemFillColorCautionBrush",
        StatusTone.Critical => "SystemFillColorCriticalBrush",
        StatusTone.Info => "SystemFillColorAttentionBrush",
        StatusTone.Neutral => "SystemFillColorNeutralBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    public static string BadgeStyleKey(StatusTone tone) => tone switch
    {
        StatusTone.Success => "BadgeSuccessStyle",
        StatusTone.Warning => "BadgeWarningStyle",
        StatusTone.Critical => "BadgeDangerStyle",
        StatusTone.Info => "BadgeInfoStyle",
        _ => "BadgeStyle",
    };

    public static Brush GetBrush(StatusTone tone) =>
        Application.Current?.TryFindResource(BrushKey(tone)) as Brush ?? FallbackBrushes[tone];

    public static Style? GetBadgeStyle(StatusTone tone) =>
        Application.Current?.TryFindResource(BadgeStyleKey(tone)) as Style;

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
