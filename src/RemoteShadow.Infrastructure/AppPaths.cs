namespace RemoteShadow.Infrastructure;

/// <summary>
/// Каталоги и файлы данных программы. Без параметра — стандартные места Windows; с <c>rootOverride</c>
/// всё лежит внутри указанного каталога (тесты и демо-режим не трогают профиль пользователя).
/// Каталоги не создаются: их создаёт тот, кто в них пишет.
/// </summary>
public sealed class AppPaths
{
    /// <param name="rootOverride">
    /// Корень данных вместо профиля. <see cref="DataDir"/> — сам этот каталог, <see cref="CacheDir"/> — подкаталог <c>cache</c>.
    /// </param>
    public AppPaths(string? rootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(rootOverride))
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteShadow");
            CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteShadow", "cache");
        }
        else
        {
            var root = Path.GetFullPath(rootOverride);
            DataDir = root;
            CacheDir = Path.Combine(root, "cache");
        }

        AuditDir = Path.Combine(DataDir, "audit");
        SettingsFile = Path.Combine(DataDir, "settings.json");
    }

    /// <summary>%AppData%\RemoteShadow: настройки и журнал.</summary>
    public string DataDir { get; }

    /// <summary>%LocalAppData%\RemoteShadow\cache: кэш индекса компьютеров (можно удалять в любой момент).</summary>
    public string CacheDir { get; }

    /// <summary>Журнал: по одному файлу yyyy-MM.jsonl на месяц.</summary>
    public string AuditDir { get; }

    public string SettingsFile { get; }
}
