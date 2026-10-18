using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Настройки в <c>settings.json</c> (JSON с отступами, camelCase). Запись атомарная: временный файл и замена.
/// Если файла нет — значения по умолчанию. Если файл испорчен — он переименовывается в <c>settings.json.bad</c>
/// (не удаляется), а программа работает со значениями по умолчанию.
/// Загруженное кэшируется; <see cref="Load"/> каждый раз возвращает новую копию, поэтому правки вызывающего
/// не попадают в общие настройки, пока он не вызовет <see cref="Save"/>.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly object _gate = new();
    private byte[]? _cachedJson;
    private volatile string? _lastError;

    public JsonSettingsStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = paths.SettingsFile;
    }

    /// <summary>Последняя ошибка чтения или записи файла; null, если всё в порядке. Ошибки не бросаются: настройки продолжают жить в памяти.</summary>
    public string? LastError => _lastError;

    public AppSettings Load()
    {
        lock (_gate)
        {
            _cachedJson ??= Serialize(ReadFromDisk());
            return Deserialize(_cachedJson);
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            var json = Serialize(Normalize(settings));
            _cachedJson = json;

            try
            {
                AtomicFile.WriteAllBytes(_file, json);
                _lastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lastError = $"Не удалось сохранить настройки: {ex.Message}";
            }
        }
    }

    private AppSettings ReadFromDisk()
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(_file)) return new AppSettings();
            bytes = File.ReadAllBytes(_file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastError = $"Не удалось прочитать настройки: {ex.Message}";
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(Utf8Bom.Skip(bytes), JsonOptions)
                           ?? throw new JsonException("Пустой документ.");
            return Normalize(settings);
        }
        catch (JsonException)
        {
            Quarantine();
            _lastError = "Файл настроек повреждён и был переименован в settings.json.bad. Используются значения по умолчанию.";
            return new AppSettings();
        }
    }

    private void Quarantine()
    {
        try
        {
            File.Move(_file, _file + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не получилось сохранить копию — оставляем как есть, следующая запись заменит файл.
        }
    }

    // Руками отредактированный файл может содержать null вместо списка.
    private static AppSettings Normalize(AppSettings settings)
    {
        settings.FavoriteComputers ??= new List<Guid>();
        settings.RecentComputers ??= new List<Guid>();
        return settings;
    }

    private static byte[] Serialize(AppSettings settings) =>
        JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);

    private static AppSettings Deserialize(byte[] json) =>
        Normalize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings());
}
