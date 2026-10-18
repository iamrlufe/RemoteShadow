using System.Text;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Журнал необработанных исключений: <c>crash.log</c> в каталоге данных программы. Пишет тип, сообщение и стек;
/// пароли и учётные данные сюда попасть не должны, потому что их нет ни в сообщениях, ни в стеке.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _directory;

    /// <summary>Каталог, пока он не задан (ошибка до построения контейнера), — <c>%AppData%\RemoteShadow</c>.</summary>
    private static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteShadow");

    public static void Initialize(string? directory) => _directory = directory;

    public static string FilePath => Path.Combine(string.IsNullOrWhiteSpace(_directory) ? DefaultDirectory : _directory, "crash.log");

    public static void Write(string source, Exception exception)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Gate)
            {
                RotateIfLarge(path);
                File.AppendAllText(path, Format(DateTime.Now, source, exception), Encoding.UTF8);
            }
        }
        catch
        {
            // Сбой журнала не должен ронять программу ещё раз.
        }
    }

    internal static string Format(DateTime now, string source, Exception exception)
    {
        var sb = new StringBuilder();
        sb.Append("=== ").Append(now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · ").AppendLine(source);
        sb.Append(exception.GetType().FullName).Append(": ").AppendLine(exception.Message);
        if (!string.IsNullOrEmpty(exception.StackTrace)) sb.AppendLine(exception.StackTrace);

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            sb.Append("  ← ").Append(inner.GetType().FullName).Append(": ").AppendLine(inner.Message);

        sb.AppendLine();
        return sb.ToString();
    }

    private static void RotateIfLarge(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;

        var old = path + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(path, old);
    }
}
