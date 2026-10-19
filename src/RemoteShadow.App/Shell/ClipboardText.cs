using System.Windows;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Копирование обычного текста (имя компьютера, логин, IP) в буфер обмена с уведомлением «Скопировано: …».
/// Секреты сюда не передаются — для них <see cref="Dialogs.SecureClipboard"/> с автоочисткой.
/// </summary>
public static class ClipboardText
{
    /// <summary>Подмена буфера обмена в тестах (там нет STA-потока и настоящего буфера).</summary>
    internal static Func<string, bool>? Setter { get; set; }

    /// <summary>Копирует текст и сообщает результат. Пустой текст не копируется.</summary>
    public static bool Copy(string? text, INotificationService notifications, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!TrySet(text))
        {
            notifications.Error("Не удалось скопировать: буфер обмена занят другой программой.");
            return false;
        }

        notifications.Success($"Скопировано{(label is null ? string.Empty : " " + label)}: {text}");
        return true;
    }

    private static bool TrySet(string text)
    {
        if (Setter is { } setter) return setter(text);

        try
        {
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch (Exception)
        {
            // Буфер обмена занят другим приложением.
            return false;
        }
    }
}
