using System.Text.Json;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Все компьютеры домена в памяти. LDAP (через <see cref="IComputerDirectory"/>) нужен только для обновления;
/// поиск и списки подразделений работают по снимку без блокировок: состав и порядок записей в снимке не меняются
/// (каждое обновление собирает новый снимок), подменяется лишь статус «в сети» отдельной записи.
/// <para>
/// Обновление. Полное — все компьютеры, затем пропавшие из выдачи удаляются. Частичное — только изменённые с
/// «последняя синхронизация минус 5 минут», результат сливается по <see cref="ComputerInfo.Id"/>: переименование и перенос
/// меняют запись, а не добавляют новую. Компьютер, удалённый или отключённый в AD, частичное обновление не видит,
/// поэтому обновление автоматически становится полным, если: полной загрузки ещё не было; сменился контроллер домена;
/// с последней полной прошло <see cref="AppSettings.FullRefreshMinutes"/> минут; изменилась настройка «показывать отключённые».
/// </para>
/// <para>
/// <see cref="RefreshAsync"/> не бросает исключения при сбое загрузки: ошибка попадает в <see cref="LastError"/>, а старые
/// данные остаются. Исключение <see cref="OperationCanceledException"/> выбрасывается только при отмене переданным токеном.
/// Одновременно выполняется одно обновление; вызов, пришедший во время другого, ждёт его и пропускается, если оно
/// успешно завершилось и было не слабее запрошенного.
/// </para>
/// <para>
/// <see cref="Changed"/> при загрузке приходит не чаще раза в 200 мс (записи публикуются пачками); частые вызовы
/// <see cref="SetOnline"/> сливаются в одно событие за тот же интервал.
/// </para>
/// </summary>
public sealed class ComputerIndex : IComputerIndex, IDisposable
{
    /// <summary>Как часто при загрузке публикуются накопленные записи (и приходит <see cref="Changed"/>).</summary>
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Запас при частичном обновлении: часы контроллера домена и этого компьютера могут расходиться.</summary>
    internal static readonly TimeSpan PartialOverlap = TimeSpan.FromMinutes(5);

    private readonly IComputerDirectory _directory;
    private readonly ISettingsStore _settings;
    private readonly ICurrentConnection _connection;
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;

    private readonly object _writeLock = new();
    private readonly object _raiseGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // Всё ниже, кроме помеченного volatile, меняется только под _writeLock.
    private ComputerSnapshot _snapshot = ComputerSnapshot.Empty;
    private DateTime? _lastSync;
    private DateTime? _lastFullSync;
    private string? _syncedDomainController;
    private bool _fullShowDisabled;
    private int _epoch;
    private CancellationTokenSource _clearCts = new();
    private long _okRuns;
    private long _okFullRuns;

    private volatile string? _lastError;
    private int _loading;
    private ITimer? _partialTimer;
    private ITimer? _fullTimer;
    private Task _background = Task.CompletedTask;

    // Слияние частых событий Changed от SetOnline; меняется только под _raiseGate.
    private ITimer? _raiseTimer;
    private bool _raiseScheduled;
    private bool _hasRaised;
    private long _lastRaise;

    public ComputerIndex(
        IComputerDirectory directory,
        ISettingsStore settings,
        ICurrentConnection connection,
        AppPaths paths,
        TimeProvider time)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ComputerInfo> All => Volatile.Read(ref _snapshot).All;

    public bool IsLoading => Volatile.Read(ref _loading) > 0;

    /// <summary>Начало последней успешной синхронизации (полной или частичной), UTC; null — синхронизации не было.</summary>
    public DateTime? LastSyncUtc
    {
        get
        {
            lock (_writeLock) return _lastSync;
        }
    }

    public string? LastError => _lastError;

    /// <summary>Фоновое обновление, запущенное <see cref="InitializeAsync"/>. Нужно тестам, чтобы дождаться его конца.</summary>
    internal Task BackgroundRefresh => _background;

    public async Task InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var epoch = Volatile.Read(ref _epoch);

        var domain = await ResolveDomainAsync(ct).ConfigureAwait(false);
        if (domain is not null) await TryLoadCacheAsync(domain, epoch, ct).ConfigureAwait(false);

        // Пока читался кэш, вызвали Clear() (смена учётной записи): эта инициализация устарела, новая придёт отдельно.
        ct.ThrowIfCancellationRequested();
        if (epoch != Volatile.Read(ref _epoch)) return;

        StartTimers(_settings.Load());
        _background = Task.Run(() => RefreshQuietlyAsync(full: false));
    }

    public async Task RefreshAsync(bool full, CancellationToken ct)
    {
        var okRuns = Interlocked.Read(ref _okRuns);
        var okFullRuns = Interlocked.Read(ref _okFullRuns);
        var epoch = Volatile.Read(ref _epoch);

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Пока мы ждали, другой вызов успел обновить индекс — повторять незачем.
            if (epoch == Volatile.Read(ref _epoch)
                && (full ? Interlocked.Read(ref _okFullRuns) != okFullRuns : Interlocked.Read(ref _okRuns) != okRuns))
            {
                return;
            }

            await RunRefreshAsync(full, ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public IReadOnlyList<ComputerInfo> Search(string text, int max = 50)
    {
        if (max <= 0 || string.IsNullOrWhiteSpace(text)) return Array.Empty<ComputerInfo>();

        var query = text.Trim().ToLowerInvariant();
        var entries = Volatile.Read(ref _snapshot).Entries;

        // Записи отсортированы по имени, поэтому внутри каждой группы порядок уже правильный.
        List<ComputerInfo>? exact = null, prefix = null, inName = null, other = null;

        foreach (var entry in entries)
        {
            var name = entry.NameLower;

            if (name.Length == query.Length && string.Equals(name, query, StringComparison.Ordinal))
            {
                Add(ref exact, entry.Info, max);
            }
            else if (name.StartsWith(query, StringComparison.Ordinal))
            {
                Add(ref prefix, entry.Info, max);
            }
            else if (name.Contains(query, StringComparison.Ordinal))
            {
                Add(ref inName, entry.Info, max);
            }
            else if (entry.DescriptionLower?.Contains(query, StringComparison.Ordinal) == true
                     || entry.DnsLower?.Contains(query, StringComparison.Ordinal) == true)
            {
                Add(ref other, entry.Info, max);
            }
        }

        var result = new List<ComputerInfo>(Math.Min(max, 64));
        foreach (var group in new[] { exact, prefix, inName, other })
        {
            if (group is null) continue;

            foreach (var info in group)
            {
                if (result.Count >= max) return result;
                result.Add(info);
            }
        }

        return result;

        static void Add(ref List<ComputerInfo>? list, ComputerInfo info, int max)
        {
            list ??= new List<ComputerInfo>();
            if (list.Count < max) list.Add(info);
        }
    }

    public IReadOnlyList<ComputerInfo> GetByOu(string ouDn, bool includeNested)
    {
        if (string.IsNullOrWhiteSpace(ouDn)) return Array.Empty<ComputerInfo>();
        return Volatile.Read(ref _snapshot).GetByOu(ouDn, includeNested);
    }

    public ComputerInfo? Find(Guid id) => Volatile.Read(ref _snapshot).Find(id);

    public void SetOnline(Guid id, bool? online)
    {
        lock (_writeLock)
        {
            var snapshot = _snapshot;
            if (!snapshot.TryGetIndex(id, out var index)) return;

            var entry = snapshot.Entries[index];
            if (entry.Info.IsOnline == online) return;

            entry.SetOnline(online);
        }

        RaiseChangedThrottled();
    }

    /// <summary>
    /// Очищает данные и метки синхронизации (кэш на диске остаётся). Идущее обновление прерывается и ничего не публикует.
    /// Таймеры продолжают работать: после нового входа обновление пойдёт само (а <see cref="InitializeAsync"/> можно вызвать повторно).
    /// </summary>
    public void Clear()
    {
        CancellationTokenSource previous;
        lock (_writeLock)
        {
            _epoch++;
            previous = _clearCts;
            _clearCts = new CancellationTokenSource();

            _snapshot = ComputerSnapshot.Empty;
            _lastSync = null;
            _lastFullSync = null;
            _syncedDomainController = null;
            _fullShowDisabled = false;
            _lastError = null;
        }

        previous.Cancel();
        RaiseChanged();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        StopTimers();

        lock (_raiseGate)
        {
            _raiseTimer?.Dispose();
            _raiseTimer = null;
        }
    }

    // ----- обновление -------------------------------------------------------------------------------------------

    private async Task RunRefreshAsync(bool requestedFull, CancellationToken ct)
    {
        int epoch;
        CancellationToken clearToken;
        DateTime? lastSync;
        bool full;

        var settings = _settings.Load();
        var startUtc = _time.GetUtcNow().UtcDateTime;
        var domainControllerBefore = _directory.DomainController;

        lock (_writeLock)
        {
            epoch = _epoch;
            clearToken = _clearCts.Token;
            lastSync = _lastSync;
            full = MustBeFull(
                requestedFull, _lastSync, _lastFullSync, _syncedDomainController, _fullShowDisabled,
                startUtc, domainControllerBefore, settings.ShowDisabledComputers,
                TimeSpan.FromMinutes(Math.Max(1, settings.FullRefreshMinutes)));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, clearToken, _lifetime.Token);
        var token = linked.Token;

        var saveCache = false;
        BeginLoading();
        RaiseChanged(); // индикатор загрузки включается сразу, а не с первой пачкой записей
        try
        {
            var query = full
                ? new ComputerQuery(null, settings.ShowDisabledComputers)
                : new ComputerQuery(lastSync!.Value - PartialOverlap, settings.ShowDisabledComputers);

            var seen = full ? new HashSet<Guid>() : null;
            var pending = new List<ComputerInfo>();
            var lastPublish = _time.GetTimestamp();

            await foreach (var computer in _directory.EnumerateAsync(query, token).WithCancellation(token).ConfigureAwait(false))
            {
                pending.Add(computer);
                seen?.Add(computer.Id);

                if (_time.GetElapsedTime(lastPublish) < PublishInterval) continue;

                if (ApplyUpdates(pending, null, epoch)) RaiseChanged();
                pending.Clear();
                lastPublish = _time.GetTimestamp();
            }

            var domainControllerAfter = _directory.DomainController;
            bool changed;
            lock (_writeLock)
            {
                // Последняя пачка и удаление пропавших — одним снимком.
                changed = ApplyUpdates(pending, seen, epoch);

                _lastSync = startUtc;
                if (full)
                {
                    _lastFullSync = startUtc;
                    _fullShowDisabled = settings.ShowDisabledComputers;
                }

                var synced = ResolveSyncedDomainController(
                    full, _syncedDomainController, domainControllerBefore, domainControllerAfter);

                // Частичное обновление пришло с другого контроллера: данные перемешаны, следующее обновление обязано быть полным
                // (даже если каталог не сообщает контроллер заранее и MustBeFull этого не заметит).
                if (!full
                    && synced is not null
                    && domainControllerAfter is not null
                    && !string.Equals(synced, domainControllerAfter, StringComparison.OrdinalIgnoreCase))
                {
                    _lastFullSync = null;
                }

                _syncedDomainController = synced;

                Interlocked.Increment(ref _okRuns);
                if (full) Interlocked.Increment(ref _okFullRuns);
            }

            _lastError = null;
            saveCache = full || changed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || epoch != Volatile.Read(ref _epoch))
        {
            // Отменил вызывающий — сообщаем ему. Отменили Clear() или Dispose() — тихо выходим.
            if (ct.IsCancellationRequested) throw;
        }
        catch (Exception ex)
        {
            _lastError = ex is OperationFailedException
                ? ex.Message
                : $"Не удалось обновить список компьютеров: {ex.Message}";
        }
        finally
        {
            EndLoading();
            RaiseChanged();
        }

        if (saveCache) await SaveCacheAsync().ConfigureAwait(false);
    }

    /// <summary>Нужна ли полная загрузка вместо частичной (см. описание класса).</summary>
    internal static bool MustBeFull(
        bool requested,
        DateTime? lastSync,
        DateTime? lastFullSync,
        string? syncedDomainController,
        bool fullShowDisabled,
        DateTime nowUtc,
        string? currentDomainController,
        bool showDisabled,
        TimeSpan fullInterval)
    {
        if (requested || lastSync is null || lastFullSync is null) return true;
        if (showDisabled != fullShowDisabled) return true;
        if (nowUtc < lastFullSync || nowUtc - lastFullSync >= fullInterval) return true;

        return currentDomainController is not null
               && syncedDomainController is not null
               && !string.Equals(currentDomainController, syncedDomainController, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// С какого контроллера считать данные полученными. Полная загрузка заменяет все данные, поэтому берём тот контроллер,
    /// который отвечал. Частичная накладывается на прежние данные: если она пришла с другого контроллера, запоминаем прежний —
    /// следующий запуск увидит расхождение и сделает полную загрузку.
    /// </summary>
    /// <param name="previous">Контроллер, с которого получены имеющиеся данные.</param>
    /// <param name="before">Контроллер каталога до загрузки.</param>
    /// <param name="after">Контроллер каталога после загрузки.</param>
    internal static string? ResolveSyncedDomainController(bool full, string? previous, string? before, string? after)
    {
        if (full) return after ?? before ?? previous;

        var reference = previous ?? before;
        if (reference is not null && after is not null && !string.Equals(reference, after, StringComparison.OrdinalIgnoreCase))
            return reference;

        return reference ?? after;
    }

    /// <summary>
    /// Сливает записи в снимок по Id; если задан <paramref name="keepOnly"/>, остальные удаляет.
    /// Возвращает false, если ничего не изменилось (тогда снимок не пересобирается).
    /// </summary>
    private bool ApplyUpdates(IReadOnlyList<ComputerInfo> incoming, HashSet<Guid>? keepOnly, int epoch)
    {
        lock (_writeLock)
        {
            if (epoch != _epoch) throw new OperationCanceledException();

            var snapshot = _snapshot;

            var latest = new Dictionary<Guid, ComputerInfo>(incoming.Count);
            foreach (var computer in incoming) latest[computer.Id] = computer;

            Dictionary<int, ComputerEntry>? replaced = null;
            List<ComputerEntry>? added = null;

            foreach (var (id, raw) in latest)
            {
                if (snapshot.TryGetIndex(id, out var index))
                {
                    var current = snapshot.Entries[index].Info;

                    // Статус «в сети» приходит не из AD — переживает обновление.
                    var merged = raw.IsOnline is null && current.IsOnline is not null
                        ? raw with { IsOnline = current.IsOnline }
                        : raw;

                    if (merged == current) continue;
                    (replaced ??= new Dictionary<int, ComputerEntry>())[index] = new ComputerEntry(merged);
                }
                else
                {
                    (added ??= new List<ComputerEntry>()).Add(new ComputerEntry(raw));
                }
            }

            var removals = 0;
            if (keepOnly is not null)
            {
                foreach (var entry in snapshot.Entries)
                {
                    if (!keepOnly.Contains(entry.Info.Id)) removals++;
                }
            }

            if (replaced is null && added is null && removals == 0) return false;

            var entries = new List<ComputerEntry>(snapshot.Entries.Length + (added?.Count ?? 0));
            for (var i = 0; i < snapshot.Entries.Length; i++)
            {
                var entry = snapshot.Entries[i];
                if (keepOnly is not null && !keepOnly.Contains(entry.Info.Id)) continue;

                entries.Add(replaced is not null && replaced.TryGetValue(i, out var changed) ? changed : entry);
            }

            if (added is not null) entries.AddRange(added);

            _snapshot = ComputerSnapshot.Build(entries);
            return true;
        }
    }

    private async Task RefreshQuietlyAsync(bool full)
    {
        try
        {
            await RefreshAsync(full, _lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            // Фоновое обновление: ошибка загрузки уже в LastError, отмена — штатное завершение.
        }
    }

    private void StartTimers(AppSettings settings)
    {
        StopTimers();

        var partial = TimeSpan.FromMinutes(Math.Max(1, settings.PartialRefreshMinutes));
        var full = TimeSpan.FromMinutes(Math.Max(1, settings.FullRefreshMinutes));

        _partialTimer = _time.CreateTimer(_ => OnTimer(false), null, partial, partial);
        _fullTimer = _time.CreateTimer(_ => OnTimer(true), null, full, full);
    }

    private void StopTimers()
    {
        _partialTimer?.Dispose();
        _fullTimer?.Dispose();
        _partialTimer = null;
        _fullTimer = null;
    }

    private void OnTimer(bool full)
    {
        // Вышли из учётной записи — обновлять нечем, ошибку «не выполнен вход» каждые 5 минут показывать незачем.
        if (_connection.Current is null || _lifetime.IsCancellationRequested) return;
        _ = Task.Run(() => RefreshQuietlyAsync(full));
    }

    private void BeginLoading() => Interlocked.Increment(ref _loading);

    private void EndLoading() => Interlocked.Decrement(ref _loading);

    private void RaiseChanged() => EventRaiser.Raise(Changed, this);

    /// <summary>
    /// Для частых мелких изменений (статус «в сети» приходит по одному компьютеру): первое событие уходит сразу, а следующие,
    /// пришедшие в течение <see cref="PublishInterval"/>, сливаются в одно событие по истечении этого времени.
    /// Подписчики (интерфейс) не получают сотни событий в секунду.
    /// </summary>
    private void RaiseChangedThrottled()
    {
        lock (_raiseGate)
        {
            if (_raiseScheduled || _lifetime.IsCancellationRequested) return;

            var wait = PublishInterval - _time.GetElapsedTime(_lastRaise);
            if (_hasRaised && wait > TimeSpan.Zero)
            {
                _raiseScheduled = true;
                _raiseTimer?.Dispose();
                _raiseTimer = _time.CreateTimer(_ => RaiseScheduled(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }

            _hasRaised = true;
            _lastRaise = _time.GetTimestamp();
        }

        RaiseChanged();
    }

    private void RaiseScheduled()
    {
        lock (_raiseGate)
        {
            if (!_raiseScheduled) return;
            _raiseScheduled = false;
            _lastRaise = _time.GetTimestamp();
        }

        if (!_lifetime.IsCancellationRequested) RaiseChanged();
    }

    // ----- кэш на диске -----------------------------------------------------------------------------------------

    /// <summary>DNS-имя домена для имени файла кэша: из подключения, а без него — из корня каталога. null, если узнать не удалось.</summary>
    private async Task<string?> ResolveDomainAsync(CancellationToken ct)
    {
        var domain = _connection.Current?.DomainName;
        if (!string.IsNullOrWhiteSpace(domain)) return domain;

        try
        {
            var dns = Dn.ToDnsDomain(await _directory.GetRootDnAsync(ct).ConfigureAwait(false));
            return dns.Length > 0 ? dns : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryLoadCacheAsync(string domain, int epoch, CancellationToken ct)
    {
        ComputerCacheFile? file;
        try
        {
            var path = Path.Combine(_paths.CacheDir, ComputerCacheFile.FileNameFor(domain));
            if (!File.Exists(path)) return;

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            file = JsonSerializer.Deserialize<ComputerCacheFile>(Utf8Bom.Skip(bytes), ComputerCacheFile.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return; // Кэш — только ускорение: повреждённый файл игнорируем, первая синхронизация его перезапишет.
        }

        if (file is null || file.Version != ComputerCacheFile.CurrentVersion || file.Computers is null) return;
        if (file.Domain is { Length: > 0 } cachedDomain && !string.Equals(cachedDomain, domain, StringComparison.OrdinalIgnoreCase)) return;

        var entries = file.Computers
            .Where(c => c is { IsValid: true })
            .GroupBy(c => c.Id)
            .Select(g => new ComputerEntry(g.Last().ToInfo()))
            .ToList();

        lock (_writeLock)
        {
            // Индекс уже наполнен (повторная инициализация или быстрый ответ AD) — кэш старее; после Clear() он чужой.
            if (epoch != _epoch || _snapshot.Entries.Length > 0 || _lastSync is not null) return;

            _snapshot = ComputerSnapshot.Build(entries);
            _lastSync = file.LastSyncUtc;
            _lastFullSync = file.LastFullSyncUtc;
            _syncedDomainController = file.DomainController;
            _fullShowDisabled = file.ShowDisabled;
        }

        RaiseChanged();
    }

    private async Task SaveCacheAsync()
    {
        try
        {
            var domain = await ResolveDomainAsync(CancellationToken.None).ConfigureAwait(false);
            if (domain is null) return;

            ComputerCacheFile file;
            lock (_writeLock)
            {
                file = new ComputerCacheFile
                {
                    Version = ComputerCacheFile.CurrentVersion,
                    Domain = domain,
                    DomainController = _syncedDomainController,
                    LastSyncUtc = _lastSync,
                    LastFullSyncUtc = _lastFullSync,
                    ShowDisabled = _fullShowDisabled,
                    Computers = _snapshot.Entries.Select(e => CachedComputer.From(e.Info)).ToList(),
                };
            }

            var path = Path.Combine(_paths.CacheDir, ComputerCacheFile.FileNameFor(domain));
            await Task.Run(() =>
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(file, ComputerCacheFile.JsonOptions);
                AtomicFile.WriteAllBytes(path, bytes);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Не удалось записать кэш — в следующий раз просто загрузимся из AD.
        }
    }
}
