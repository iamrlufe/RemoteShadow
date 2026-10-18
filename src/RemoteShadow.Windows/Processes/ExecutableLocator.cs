namespace RemoteShadow.Windows;

/// <summary>Поиск исполняемого файла: System32, затем PATH, затем WindowsApps (там лежит wt.exe).</summary>
internal static class ExecutableLocator
{
    /// <summary>
    /// Полный путь к программе или null.
    /// Имя с путём (C:\Tools\x.exe) принимается только абсолютным и только если файл существует: подменять его
    /// файлом из другого каталога нельзя. Относительные пути с разделителями (sub\x.exe) отвергаются, чтобы результат
    /// не зависел от текущего каталога. Имя без пути ищется по каталогам <paramref name="searchDirectories"/> по порядку.
    /// Без расширения добавляется «.exe».
    /// </summary>
    public static string? Find(string? fileName, IEnumerable<string> searchDirectories, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        fileName = fileName.Trim().Trim('"');
        if (fileName.Length == 0 || fileName.Contains('\0')) return null;

        var hasDirectory = fileName.IndexOfAny(['\\', '/']) >= 0 || fileName.Contains(':');
        if (hasDirectory)
        {
            if (!Path.IsPathFullyQualified(fileName)) return null;
            var full = WithDefaultExtension(fileName);
            return IsValidFileName(Path.GetFileName(full)) && fileExists(full) ? full : null;
        }

        var name = WithDefaultExtension(fileName);
        if (!IsValidFileName(name)) return null;

        foreach (var directory in searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate;
            try
            {
                candidate = Path.Combine(directory, name);
            }
            catch (ArgumentException)
            {
                continue; // недопустимые символы в элементе PATH
            }

            if (fileExists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Каталоги поиска по порядку: System32, каталог Windows, PATH, %LOCALAPPDATA%\Microsoft\WindowsApps.</summary>
    public static IReadOnlyList<string> DefaultSearchDirectories()
    {
        var result = new List<string>();

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            var trimmed = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
            if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed)) return;
            if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) result.Add(trimmed);
        }

        Add(Environment.SystemDirectory);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
            Add(entry);

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));

        return result;
    }

    /// <summary>"mstsc" → "mstsc.exe"; имя с расширением не меняется.</summary>
    public static string WithDefaultExtension(string fileName) =>
        Path.HasExtension(fileName) ? fileName : fileName + ".exe";

    private static bool IsValidFileName(string name) =>
        name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
