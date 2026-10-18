using System.Windows;

namespace RemoteShadow.App.Shell;

/// <summary>Применяет тему из <c>AppSettings.Theme</c> («System», «Light», «Dark»). Страница настроек может вызвать <see cref="Apply"/> сразу после выбора.</summary>
public sealed class ThemeService
{
    public void Apply(string? theme)
    {
        var mode = ToThemeMode(theme);
        var dispatcher = UiThread.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) Application.Current.ThemeMode = mode;
        else dispatcher.BeginInvoke(() => Application.Current.ThemeMode = mode);
    }

    internal static ThemeMode ToThemeMode(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}
