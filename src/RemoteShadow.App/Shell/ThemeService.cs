using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace RemoteShadow.App.Shell;

/// <summary>Применяет тему из <c>AppSettings.Theme</c> («System», «Light», «Dark»). Страница настроек может вызвать <see cref="Apply"/> сразу после выбора.
/// Заодно подставляет фирменную палитру (индиго) под эффективную тему: ключи Fluent переопределяются в Application.Resources.</summary>
public sealed class ThemeService
{
    private string? _theme;
    private bool _subscribed;

    public void Apply(string? theme)
    {
        _theme = theme;
        var mode = ToThemeMode(theme);
        var dispatcher = UiThread.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) ApplyCore(mode);
        else dispatcher.BeginInvoke(() => ApplyCore(mode));
    }

    private void ApplyCore(ThemeMode mode)
    {
        Application.Current.ThemeMode = mode;
        ApplyPalette(IsDark(mode));

        // В режиме «System» следим за переключением темы Windows.
        if (!_subscribed)
        {
            _subscribed = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (ToThemeMode(_theme) != ThemeMode.System) return;
        UiThread.Dispatcher?.BeginInvoke(() => ApplyPalette(IsDark(ThemeMode.System)));
    }

    private static bool IsDark(ThemeMode mode)
    {
        if (mode == ThemeMode.Dark) return true;
        if (mode == ThemeMode.Light) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyPalette(bool dark)
    {
        var res = Application.Current.Resources;
        void Set(string key, string color) => res[key] = Frozen(color);
        void SetColor(string key, string color) => res[key] = (Color)ColorConverter.ConvertFromString(color);

        // Системные акцентные цвета: от них Fluent считает подчёркивание фокуса в полях, индикаторы выполнения и т. п.
        string[] accent = dark
            ? ["#818CF8", "#A5B4FC", "#6366F1", "#C7D2FE", "#A5B4FC", "#818CF8", "#4338CA", "#3730A3", "#312E81"]
            : ["#4F46E5", "#4338CA", "#3730A3", "#818CF8", "#6366F1", "#4F46E5", "#4338CA", "#3730A3", "#312E81"];
        SetColor("SystemAccentColor", accent[0]);
        SetColor("SystemAccentColorPrimary", accent[0]);
        SetColor("SystemAccentColorSecondary", accent[1]);
        SetColor("SystemAccentColorTertiary", accent[2]);
        SetColor("SystemAccentColorLight1", accent[3]);
        SetColor("SystemAccentColorLight2", accent[4]);
        SetColor("SystemAccentColorLight3", accent[5]);
        SetColor("SystemAccentColorDark1", accent[6]);
        SetColor("SystemAccentColorDark2", accent[7]);
        SetColor("SystemAccentColorDark3", accent[8]);

        if (dark)
        {
            Set("AccentFillColorDefaultBrush", "#818CF8");
            Set("AccentFillColorSecondaryBrush", "#A5B4FC");
            Set("AccentFillColorTertiaryBrush", "#6366F1");
            Set("AccentFillColorDisabledBrush", "#3B3F66");
            Set("AccentTextFillColorPrimaryBrush", "#A5B4FC");
            Set("AccentTextFillColorSecondaryBrush", "#C7D2FE");
            Set("AccentTextFillColorTertiaryBrush", "#818CF8");
            Set("TextOnAccentFillColorPrimaryBrush", "#0F172A");
            Set("TextOnAccentFillColorDisabledBrush", "#80869F");
            Set("RsAccentSubtleBrush", "#26818CF8");
            Set("RsAccentSubtleHoverBrush", "#33818CF8");
            Set("RsFocusRingBrush", "#A5B4FC");
            Set("RsTextMutedBrush", "#B4BAC8");
        }
        else
        {
            Set("AccentFillColorDefaultBrush", "#4F46E5");
            Set("AccentFillColorSecondaryBrush", "#4338CA");
            Set("AccentFillColorTertiaryBrush", "#3730A3");
            Set("AccentFillColorDisabledBrush", "#A5A8D8");
            Set("AccentTextFillColorPrimaryBrush", "#4338CA");
            Set("AccentTextFillColorSecondaryBrush", "#3730A3");
            Set("AccentTextFillColorTertiaryBrush", "#6366F1");
            Set("TextOnAccentFillColorPrimaryBrush", "#FFFFFF");
            Set("TextOnAccentFillColorDisabledBrush", "#E8E9F7");
            Set("RsAccentSubtleBrush", "#1A4F46E5");
            Set("RsAccentSubtleHoverBrush", "#264F46E5");
            Set("RsFocusRingBrush", "#4F46E5");
            Set("RsTextMutedBrush", "#4B5563");
        }
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal static ThemeMode ToThemeMode(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}
