using System.Windows;
using System.Windows.Threading;

namespace RemoteShadow.App.Shell.Dialogs;

/// <summary>
/// Копирование секрета в буфер обмена с автоочисткой: через заданное время буфер очищается, если в нём
/// по-прежнему этот же текст. Также просит Windows не сохранять копию в журнале буфера (Win+V) и не отправлять
/// в облако.
/// </summary>
internal static class SecureClipboard
{
    private static DispatcherTimer? _timer;
    private static string? _pending;

    /// <summary>true — скопировано. Ошибка буфера обмена (занят другим приложением) возвращает false.</summary>
    public static bool CopyWithAutoClear(string text, TimeSpan clearAfter)
    {
        if (!TrySet(text)) return false;

        _pending = text;
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = clearAfter };
        _timer.Tick += (_, _) => ClearPending();
        _timer.Start();
        return true;
    }

    /// <summary>Очищает буфер, если там всё ещё скопированный секрет. Вызывается по таймеру и при выходе из программы.</summary>
    public static void ClearPending()
    {
        _timer?.Stop();
        _timer = null;

        var secret = _pending;
        _pending = null;
        if (secret is null) return;

        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear();
        }
        catch (Exception)
        {
            // Буфер обмена занят: очистить не удалось, повторять не будем.
        }
    }

    private static bool TrySet(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text);
            try
            {
                // Форматы Windows для менеджеров буфера обмена: значение 0 запрещает попадание в историю и в облако.
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            }
            catch (Exception)
            {
                // Дополнительные форматы не удались: обычное копирование всё равно сработает.
            }

            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (Exception)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
