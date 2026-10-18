using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Журнал в формате JSON Lines: один файл на месяц (<c>AuditDir\yyyy-MM.jsonl</c>), одна запись — одна строка.
/// Запись только дописыванием. <see cref="WriteAsync"/> никогда не бросает исключения: сбой журнала не должен отменять
/// действие, а причина последнего сбоя доступна в <see cref="LastError"/>.
/// Если в настройках задана <see cref="AppSettings.AuditCentralFolder"/>, та же строка дописывается ещё и в файл
/// <c>&lt;каталог&gt;\&lt;компьютер&gt;-yyyy-MM.jsonl</c>; ошибка центральной копии локальную запись не затрагивает.
/// Месяц файла определяется по <see cref="AuditRecord.TimeUtc"/> (UTC).
/// </summary>
public sealed class JsonAuditLog : IAuditLog
{
    private const string MonthFormat = "yyyy-MM";
    private const string FileExtension = ".jsonl";

    /// <summary>Допуск при досрочной остановке чтения по «since»: записи двух экземпляров программы могут чуть перемешаться по времени.</summary>
    private static readonly TimeSpan OrderTolerance = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Журнал читают глазами: кириллица без \uXXXX. Файл не попадает в HTML, поэтому смягчённое экранирование безопасно.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly TimeSpan _centralTimeout;
    private readonly TimeSpan _centralCooldown;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _lastError;

    // Сбой центральной копии (меняются только под _gate): пока идёт пауза, копию не пишем и не ждём сеть на каждой записи.
    private string? _centralFailure;
    private long _centralFailedAt;

    public JsonAuditLog(AppPaths paths, ISettingsStore settings)
        : this(paths, settings, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
    {
    }

    /// <param name="centralTimeout">Сколько ждать запись в общую папку.</param>
    /// <param name="centralCooldown">Сколько не обращаться к общей папке после сбоя.</param>
    internal JsonAuditLog(AppPaths paths, ISettingsStore settings, TimeSpan centralTimeout, TimeSpan centralCooldown)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;
        _centralTimeout = centralTimeout;
        _centralCooldown = centralCooldown;
    }

    /// <summary>Причина последнего сбоя записи (локальной или центральной копии); null, если последняя запись прошла полностью.</summary>
    public string? LastError => _lastError;

    public async Task WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(record);

            var line = SerializeLine(record);
            var month = MonthOf(record.TimeUtc);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var localError = await TryAppendLocalAsync(month, line).ConfigureAwait(false);
                var centralError = await TryAppendCentralAsync(record, month, line).ConfigureAwait(false);
                _lastError = localError ?? centralError;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _lastError = Describe("Не удалось записать журнал", ex);
        }
    }

    public async Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int max, DateTime? sinceUtc = null, CancellationToken ct = default)
    {
        if (max <= 0) return Array.Empty<AuditRecord>();

        var since = sinceUtc is { } s ? AsUtc(s) : (DateTime?)null;
        var result = new List<AuditRecord>(Math.Min(max, 256));

        foreach (var (monthStart, path) in ListMonthFiles())
        {
            ct.ThrowIfCancellationRequested();

            // Файлы идут от нового месяца к старому: если весь месяц раньше границы, старше него искать нечего.
            if (since is { } border && monthStart.AddMonths(1) <= border) break;

            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

                await foreach (var line in ReverseLineReader.ReadLinesAsync(stream, ct).ConfigureAwait(false))
                {
                    var record = TryParse(line);
                    if (record is null) continue;

                    if (since is { } lowerBound)
                    {
                        var time = AsUtc(record.TimeUtc);
                        if (time < lowerBound - OrderTolerance) break;
                        if (time < lowerBound) continue;
                    }

                    result.Add(record);
                    if (result.Count >= max) return Ordered(result);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл заблокирован или недоступен — читаем остальные месяцы.
            }
        }

        return Ordered(result);
    }

    private static IReadOnlyList<AuditRecord> Ordered(List<AuditRecord> records) =>
        records.OrderByDescending(r => AsUtc(r.TimeUtc)).ToList();

    private IEnumerable<(DateTime MonthStart, string Path)> ListMonthFiles()
    {
        string[] files;
        try
        {
            if (!Directory.Exists(_paths.AuditDir)) return Array.Empty<(DateTime, string)>();
            files = Directory.GetFiles(_paths.AuditDir, "*" + FileExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<(DateTime, string)>();
        }

        var list = new List<(DateTime, string)>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParseExact(name, MonthFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                list.Add((month, file));
        }

        return list.OrderByDescending(f => f.Item1);
    }

    private async Task<string?> TryAppendLocalAsync(string month, byte[] line)
    {
        try
        {
            await AppendAsync(Path.Combine(_paths.AuditDir, month + FileExtension), line, createDirectory: true).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return Describe("Не удалось записать журнал", ex);
        }
    }

    private async Task<string?> TryAppendCentralAsync(AuditRecord record, string month, byte[] line)
    {
        string? folder;
        try
        {
            folder = _settings.Load().AuditCentralFolder;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(folder)) return null;

        // Общая папка недавно не ответила: не ждём её на каждой записи (массовое действие иначе встало бы на минуты).
        if (_centralFailure is not null && Stopwatch.GetElapsedTime(_centralFailedAt) < _centralCooldown)
            return _centralFailure;

        var failure = await TryAppendCentralCoreAsync(folder, record, month, line).ConfigureAwait(false);
        _centralFailure = failure;
        if (failure is not null) _centralFailedAt = Stopwatch.GetTimestamp();
        return failure;
    }

    private async Task<string?> TryAppendCentralCoreAsync(string folder, AuditRecord record, string month, byte[] line)
    {
        try
        {
            var path = Path.Combine(folder, $"{SafeFileName(record.Machine)}-{month}{FileExtension}");

            // Недоступная сетевая папка может блокировать вызов надолго, а отменить его нельзя: не ждём дольше таймаута.
            var append = Task.Run(() => AppendAsync(path, line, createDirectory: false, appendOnly: true));
            using var timeoutCts = new CancellationTokenSource();
            var finished = await Task.WhenAny(append, Task.Delay(_centralTimeout, timeoutCts.Token)).ConfigureAwait(false);

            if (finished != append)
            {
                // Результат уже никому не нужен, но исключение надо «прочитать», чтобы оно не стало необработанным.
                _ = append.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return $"Центральная копия журнала: нет ответа от общей папки за {_centralTimeout.TotalSeconds:0.#} с.";
            }

            await timeoutCts.CancelAsync().ConfigureAwait(false);
            await append.ConfigureAwait(false);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return $"Центральная копия журнала: общая папка недоступна ({folder}).";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Центральная копия журнала: нет права дописывать файлы в общей папке ({folder}).";
        }
        catch (Exception ex)
        {
            return Describe("Не удалось записать центральную копию журнала", ex);
        }
    }

    // FileShare.Read: пока идёт запись, другие могут только читать. Два экземпляра программы пишут по очереди,
    // поэтому при занятом файле коротко повторяем. Недостающие папки создаём только для локального журнала:
    // опечатка в пути общей папки не должна порождать каталоги, и сам FileStream сообщит, что пути нет.
    private static async Task AppendAsync(string path, byte[] data, bool createDirectory, bool appendOnly = false)
    {
        if (createDirectory) Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = OpenForAppend(path, appendOnly);
                await stream.WriteAsync(data).ConfigureAwait(false);
                return;
            }
            catch (IOException ex) when (attempt < 4 && IsSharingViolation(ex))
            {
                await Task.Delay(30 * attempt).ConfigureAwait(false);
            }
        }
    }

    private static FileStream OpenForAppend(string path, bool appendOnly)
    {
        if (appendOnly && OperatingSystem.IsWindows())
        {
            // Общая папка с правами «создавать и дописывать» не даёт менять уже записанное. Обычное открытие на запись просит
            // ещё и право на перезапись данных, такую папку оно не пройдёт, поэтому запрашиваем ровно право дописывать.
            return new FileInfo(path).Create(FileMode.Append, FileSystemRights.AppendData, FileShare.Read, 1, FileOptions.Asynchronous, null);
        }

        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1, useAsync: true);
    }

    // ERROR_SHARING_VIOLATION (32) и ERROR_LOCK_VIOLATION (33): файл на миг занят другим экземпляром программы.
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static byte[] SerializeLine(AuditRecord record)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        var line = new byte[json.Length + 1];
        Buffer.BlockCopy(json, 0, line, 0, json.Length);
        line[^1] = (byte)'\n';
        return line;
    }

    private static AuditRecord? TryParse(byte[] line)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditRecord>(Utf8Bom.Skip(line), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MonthOf(DateTime timeUtc) =>
        AsUtc(timeUtc).ToString(MonthFormat, CultureInfo.InvariantCulture);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;

    internal static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var safe = sb.ToString().Trim('.');
        return safe.Length == 0 ? "unknown" : safe;
    }

    private static string Describe(string prefix, Exception ex) => $"{prefix}: {ex.Message}";
}
