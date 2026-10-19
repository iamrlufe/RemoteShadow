using System.Text.Json;
using System.Windows;
using RemoteShadow.Infrastructure;

namespace RemoteShadow.App.Shell;

/// <summary>Положение и размер главного окна и последний открытый раздел. Хранятся отдельно от настроек: это состояние интерфейса, а не параметры работы.</summary>
public sealed record MainWindowPlacement(double Left, double Top, double Width, double Height, bool Maximized, string? LastPageId);

/// <summary>
/// Читает и пишет <c>window.json</c> в каталоге данных программы. Ошибки чтения и записи не мешают работе: окно просто
/// откроется в положении по умолчанию.
/// </summary>
public sealed class WindowStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public WindowStateStore(AppPaths paths) : this(Path.Combine(paths.DataDir, "window.json"))
    {
    }

    internal WindowStateStore(string path) => _path = path;

    public MainWindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<MainWindowPlacement>(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Чтение положения окна", ex);
            return null;
        }
    }

    public void Save(MainWindowPlacement state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Сохранение положения окна", ex);
        }
    }

    /// <summary>
    /// Прямоугольник окна, если он заметно виден на рабочем столе (монитор могли отключить), иначе null. Размер
    /// не меньше минимального размера окна.
    /// </summary>
    internal static Rect? FitToScreen(MainWindowPlacement state, Rect virtualScreen, Size minSize)
    {
        if (double.IsNaN(state.Width) || double.IsNaN(state.Height) || state.Width <= 0 || state.Height <= 0) return null;

        var rect = new Rect(state.Left, state.Top, Math.Max(state.Width, minSize.Width), Math.Max(state.Height, minSize.Height));
        var visible = Rect.Intersect(rect, virtualScreen);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 100) return null;

        return rect;
    }
}
