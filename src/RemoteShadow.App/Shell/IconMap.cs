using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Справочник значков: имя в стиле Tabler (так значки называют <c>IAction.Icon</c> и <c>IPage.Icon</c>) → глиф шрифта
/// Segoe Fluent Icons. Неизвестное имя даёт универсальный значок, а не пустоту.
/// </summary>
public static class IconMap
{
    /// <summary>Segoe Fluent Icons (Windows 11), запасной — Segoe MDL2 Assets (Windows 10).</summary>
    public static readonly FontFamily FontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Универсальный значок («…»), когда имя не найдено.</summary>
    public const string Fallback = "";

    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["device-desktop"] = "",
        ["terminal-2"] = "",
        ["folder"] = "",
        ["eye"] = "",
        ["mouse"] = "",
        ["message"] = "",
        ["plug-x"] = "",
        ["logout"] = "",
        ["refresh"] = "",
        ["power"] = "",
        ["player-stop"] = "",
        ["key"] = "",
        ["lock-open"] = "",
        ["user-check"] = "",
        ["user-off"] = "",
        ["users-plus"] = "",
        ["users-minus"] = "",
        ["stethoscope"] = "",
        ["tool"] = "",
        ["users"] = "",
        ["star"] = "",
        ["history"] = "",
        ["settings"] = "",
        ["search"] = "",
        ["chevron-down"] = "",
        ["chevron-right"] = "",
        ["alert-triangle"] = "",
        ["check"] = "",
        ["x"] = "",
        ["info-circle"] = "",
        ["copy"] = "",
        ["eye-off"] = "",
        ["building"] = "",
        ["server"] = "",
        ["filter"] = "",

        // Дополнительные значки оболочки.
        ["user"] = "",
        ["circle-check"] = "",
        ["circle-x"] = "",
        ["alert-triangle-filled"] = "",
    };

    /// <summary>Глиф по имени Tabler. Если передан уже сам глиф (один символ из области частного использования) — возвращается как есть.</summary>
    public static string Get(string? tablerName)
    {
        if (string.IsNullOrWhiteSpace(tablerName)) return Fallback;

        var name = tablerName.Trim();
        if (name.Length == 1 && name[0] is >= '' and <= '') return name;

        if (name.StartsWith("ti-", StringComparison.OrdinalIgnoreCase)) name = name[3..];
        return Glyphs.TryGetValue(name, out var glyph) ? glyph : Fallback;
    }

    /// <summary>Готовый <see cref="TextBlock"/> со значком — для кода (пункты меню, динамические элементы).</summary>
    public static TextBlock CreateIcon(string? tablerName, double size = 14) => new()
    {
        Text = Get(tablerName),
        FontFamily = FontFamily,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
}
