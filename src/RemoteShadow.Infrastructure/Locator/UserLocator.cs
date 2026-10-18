using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Ищет, где сейчас работает пользователь. <see cref="FindCached"/> смотрит только в кэш сессий. <see cref="FindAsync"/> сначала
/// отдаёт совпадения из кэша (<see cref="UserLocation.FromCache"/> = true), затем опрашивает компьютеры и отдаёт находки по
/// мере ответов, не дожидаясь остальных. Один и тот же сеанс может прийти дважды — из кэша, потом живым; потребитель
/// заменяет запись по паре <c>Computer.Id</c> и <c>Session.Id</c>.
/// Текст разрешается в логины через <see cref="IUserDirectory"/> (по фамилии находится логин); ФИО найденных пользователей
/// запоминаются, и по ним потом ищет <see cref="FindCached"/>. Сначала проверяется порт 445 (не больше
/// <see cref="AppSettings.OnlineProbeParallelism"/> проверок сразу), сессии запрашиваются только у компьютеров в сети,
/// одновременных запросов сессий — не больше min(32, <see cref="AppSettings.WtsThreads"/>). Результат опроса кладётся
/// в <see cref="ISessionCache"/>; неудачный опрос не затирает прежние удачные данные компьютера.
/// </summary>
public sealed class UserLocator : IUserLocator
{
    private const int MaxResolvedUsers = 20;

    // Быстрая проверка порта дешёвая, а WTS-запрос занимает поток пула, поэтому лимиты разные.
    private const int MaxProbeParallelism = 128;
    private const int MaxSessionParallelism = 32;

    private readonly IComputerIndex _index;
    private readonly ISessionService _sessions;
    private readonly ISessionCache _cache;
    private readonly IUserDirectory _users;
    private readonly IHostProbe _probe;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;

    /// <summary>Логин → ФИО, из ранее найденных в AD пользователей.</summary>
    private readonly ConcurrentDictionary<string, string> _fullNames = new(StringComparer.OrdinalIgnoreCase);

    public UserLocator(
        IComputerIndex index,
        ISessionService sessions,
        ISessionCache cache,
        IUserDirectory users,
        IHostProbe probe,
        ISettingsStore settings,
        TimeProvider time)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public IReadOnlyList<UserLocation> FindCached(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<UserLocation>();

        var query = text.Trim();
        var found = new List<UserLocation>();

        foreach (var snapshot in _cache.GetAll())
        {
            var computer = _index.Find(snapshot.ComputerId);
            if (computer is null) continue;

            foreach (var session in snapshot.Result.Sessions)
            {
                if (SessionMatcher.Matches(session, query, null, _fullNames))
                    found.Add(new UserLocation(computer, session, snapshot.TakenAt, FromCache: true));
            }
        }

        return found
            .OrderByDescending(l => l.SeenAt)
            .ThenBy(l => l.Computer.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Session.Id)
            .ToList();
    }

    public async IAsyncEnumerable<UserLocation> FindAsync(
        string text,
        string? scopeOuDn,
        bool includeNestedOu,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        ct.ThrowIfCancellationRequested();
        var query = text.Trim();

        foreach (var cached in FindCached(query)) yield return cached;

        var logins = await ResolveLoginsAsync(query, ct).ConfigureAwait(false);

        var candidates = (scopeOuDn is null ? _index.All : _index.GetByOu(scopeOuDn, includeNestedOu))
            .Where(c => c.Enabled && HostNameValidator.IsValid(c.HostName))
            .OrderByDescending(c => c.IsOnline == true) // известные «в сети» — первыми: находки придут раньше
            .ToList();
        if (candidates.Count == 0) yield break;

        var settings = _settings.Load();
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(settings.OnlineProbeTimeoutMs, 100, 5_000));
        var probeParallelism = Math.Clamp(settings.OnlineProbeParallelism, 1, MaxProbeParallelism);
        var sessionParallelism = Math.Clamp(Math.Min(MaxSessionParallelism, settings.WtsThreads), 1, MaxSessionParallelism);

        var channel = Channel.CreateUnbounded<UserLocation>(new UnboundedChannelOptions { SingleReader = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sessionSlots = new SemaphoreSlim(sessionParallelism);
        var producer = Task.Run(
            () => ProduceAsync(candidates, query, logins, timeout, probeParallelism, sessionSlots, channel.Writer, cts.Token),
            CancellationToken.None);

        try
        {
            await foreach (var location in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return location;
            }
        }
        finally
        {
            // Читатель ушёл (отмена или break): останавливаем опрос и дожидаемся рабочих, чтобы не оставлять задач в фоне.
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch
            {
                // Ошибки производителя уже переданы читателю через канал.
            }
        }
    }

    /// <summary>
    /// Логины, соответствующие тексту. Ошибка поиска в AD не фатальна: тогда остаётся поиск по тексту как по логину.
    /// </summary>
    private async Task<HashSet<string>> ResolveLoginsAsync(string text, CancellationToken ct)
    {
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var users = await _users.SearchAsync(text, MaxResolvedUsers, ct).ConfigureAwait(false);
            foreach (var user in users)
            {
                if (string.IsNullOrWhiteSpace(user.SamAccountName)) continue;

                logins.Add(user.SamAccountName);
                if (!string.IsNullOrWhiteSpace(user.DisplayName)) _fullNames[user.SamAccountName] = user.DisplayName;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // AD недоступен или нет прав на поиск пользователей.
        }

        return logins;
    }

    private async Task ProduceAsync(
        List<ComputerInfo> candidates,
        string query,
        HashSet<string> logins,
        TimeSpan probeTimeout,
        int probeParallelism,
        SemaphoreSlim sessionSlots,
        ChannelWriter<UserLocation> writer,
        CancellationToken ct)
    {
        try
        {
            var next = -1;
            var workers = Enumerable.Range(0, Math.Min(probeParallelism, candidates.Count))
                .Select(_ => Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var i = Interlocked.Increment(ref next);
                        if (i >= candidates.Count) return;

                        await ScanAsync(candidates[i], query, logins, probeTimeout, sessionSlots, writer, ct).ConfigureAwait(false);
                    }
                }, CancellationToken.None))
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    private async Task ScanAsync(
        ComputerInfo computer,
        string query,
        HashSet<string> logins,
        TimeSpan probeTimeout,
        SemaphoreSlim sessionSlots,
        ChannelWriter<UserLocation> writer,
        CancellationToken ct)
    {
        try
        {
            if (!await _probe.IsOnlineAsync(computer.HostName, probeTimeout, ct).ConfigureAwait(false)) return;

            SessionListResult result;
            await sessionSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Пока ждали место, поиск могли отменить: тогда блокирующий WTS-вызов уже не начинаем.
                ct.ThrowIfCancellationRequested();
                result = await _sessions.GetSessionsAsync(computer.HostName, ct).ConfigureAwait(false);
            }
            finally
            {
                sessionSlots.Release();
            }

            var seenAt = _time.GetUtcNow().UtcDateTime;
            StoreInCache(computer, result, seenAt);

            foreach (var session in result.Sessions)
            {
                if (SessionMatcher.Matches(session, query, logins, _fullNames))
                    writer.TryWrite(new UserLocation(computer, session, seenAt, FromCache: false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Поиск отменён.
        }
        catch
        {
            // Один недоступный или упавший хост не должен останавливать поиск по остальным.
        }
    }

    // Неудачный опрос не затирает прежние хорошие данные: «где пользователь был в последний раз» важнее, чем «хост не ответил».
    private void StoreInCache(ComputerInfo computer, SessionListResult result, DateTime seenAt)
    {
        if (!result.IsOk && _cache.Get(computer.Id) is { Result.IsOk: true }) return;

        _cache.Set(new SessionSnapshot(computer.Id, result, seenAt));
    }
}

/// <summary>Правило совпадения сессии с искомым пользователем.</summary>
internal static class SessionMatcher
{
    /// <summary>
    /// Сессии без пользователя не подходят. Подходит сессия, если её логин входит в <paramref name="logins"/> (точное совпадение),
    /// либо логин или CORP\логин содержит <paramref name="text"/>, либо известное ФИО владельца логина содержит <paramref name="text"/>.
    /// Без учёта регистра.
    /// </summary>
    public static bool Matches(
        SessionInfo session,
        string text,
        IReadOnlySet<string>? logins,
        IReadOnlyDictionary<string, string> fullNames)
    {
        if (!session.HasUser) return false;

        var login = session.UserName!;
        if (logins is not null && logins.Contains(login)) return true;
        if (login.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (session.AccountName.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;

        return fullNames.TryGetValue(login, out var fullName)
               && fullName.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
