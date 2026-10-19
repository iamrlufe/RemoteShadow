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
