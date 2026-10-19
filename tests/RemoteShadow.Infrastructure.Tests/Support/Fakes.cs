using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Infrastructure.Tests;

/// <summary>Каталог компьютеров с изменяемым набором, постраничной выдачей и точками остановки для тестов.</summary>
public sealed class FakeComputerDirectory : IComputerDirectory
{
    private readonly object _gate = new();
    private readonly List<ComputerQuery> _queries = new();

    public List<ComputerInfo> Computers { get; } = new();

    public string? DomainController { get; set; } = "dc1.corp.local";

    public int PageSize { get; set; } = 500;

    /// <summary>Если задано, выдача ждёт этой задачи перед первой страницей.</summary>
    public Task? Gate { get; set; }

    /// <summary>Выбрасывается в начале выдачи (после Gate).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>По номеру вызова (с 1) решает, какое исключение выбросить в начале выдачи; null — не бросать.</summary>
    public Func<int, Exception?>? FailOnCall { get; set; }

    /// <summary>Выбрасывается после выдачи указанного числа записей.</summary>
    public (int After, Exception Error)? FailAfter { get; set; }

    /// <summary>Вызывается после каждой страницы (номер страницы с нуля).</summary>
    public Func<int, CancellationToken, Task>? AfterPage { get; set; }

    /// <summary>Завершается, когда началась очередная выдача.</summary>
    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<ComputerQuery> Queries
    {
        get
        {
            lock (_gate) return _queries.ToList();
        }
    }

    public int EnumerateCalls => Queries.Count;

    public Task<string> GetRootDnAsync(CancellationToken ct) => Task.FromResult(TestData.Root);

    public async IAsyncEnumerable<ComputerInfo> EnumerateAsync(ComputerQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        int call;
        lock (_gate)
        {
            _queries.Add(query);
            call = _queries.Count;
        }

        Entered.TrySetResult();

        if (Gate is { } gate) await gate.WaitAsync(ct);
        if (FailWith is { } failure) throw failure;
        if (FailOnCall?.Invoke(call) is { } callFailure) throw callFailure;

        List<ComputerInfo> snapshot;
        lock (_gate)
        {
            snapshot = Computers
                .Where(c => query.IncludeDisabled || c.Enabled)
                .Where(c => query.ChangedSince is null || (c.WhenChanged is { } changed && changed >= query.ChangedSince))
                .ToList();
        }

        var page = 0;
        var inPage = 0;
        var total = 0;
        foreach (var computer in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            if (FailAfter is { } fail && total >= fail.After) throw fail.Error;

            yield return computer;
            total++;

            if (++inPage < PageSize) continue;
            inPage = 0;
            if (AfterPage is { } after) await after(page, ct);
            page++;
        }
    }

    public Task<IReadOnlyList<OuNode>> GetOuChildrenAsync(string parentDn, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OuNode>>(Array.Empty<OuNode>());

    public void ResetEntered() => Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class FakeHostProbe : IHostProbe
{
    private int _current;
    private int _max;

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Таймаут, с которым вызывали проверку, по порядку вызовов.</summary>
    public ConcurrentQueue<TimeSpan> Timeouts { get; } = new();

    /// <summary>Порты, с которыми вызывали <see cref="IsPortOpenAsync"/> (IsOnlineAsync пишет сюда 445).</summary>
    public ConcurrentQueue<int> Ports { get; } = new();

    public Func<string, bool> Online { get; set; } = _ => true;

    /// <summary>Выполняется внутри проверки, до ответа. Здесь тест может держать проверку открытой.</summary>
    public Func<string, CancellationToken, Task>? Hold { get; set; }

    public HashSet<string> Throwing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int MaxConcurrent => Volatile.Read(ref _max);

    /// <summary>Сколько вызовов выполняется прямо сейчас.</summary>
    public int Active => Volatile.Read(ref _current);

    public Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        Ports.Enqueue(port);
        return IsOnlineAsync(host, timeout, ct);
    }

    public async Task<bool> IsOnlineAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _max)))
        {
            if (Interlocked.CompareExchange(ref _max, now, seen) == seen) break;
        }

        try
        {
            Calls.Enqueue(host);
            Timeouts.Enqueue(timeout);
            if (Hold is { } hold) await hold(host, ct);
            ct.ThrowIfCancellationRequested();
            if (Throwing.Contains(host)) throw new InvalidOperationException("probe failed");
            return Online(host);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}

public sealed class FakeSessionService : ISessionService
{
    private int _current;
    private int _max;

    public ConcurrentQueue<string> Calls { get; } = new();

    public Func<string, SessionListResult> Respond { get; set; } = _ => SessionListResult.Ok(Array.Empty<SessionInfo>());

    public Func<string, CancellationToken, Task>? Hold { get; set; }

    public int MaxConcurrent => Volatile.Read(ref _max);

    /// <summary>Сколько вызовов выполняется прямо сейчас.</summary>
    public int Active => Volatile.Read(ref _current);

    public async Task<SessionListResult> GetSessionsAsync(string host, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _max)))
        {
            if (Interlocked.CompareExchange(ref _max, now, seen) == seen) break;
        }

        try
        {
            Calls.Enqueue(host);
            if (Hold is { } hold) await hold(host, ct);
            ct.ThrowIfCancellationRequested();
            return Respond(host);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    public Task SendMessageAsync(string host, int sessionId, string title, string text, CancellationToken ct) => throw new NotSupportedException();

    public Task DisconnectAsync(string host, int sessionId, CancellationToken ct) => throw new NotSupportedException();

    public Task LogoffAsync(string host, int sessionId, CancellationToken ct) => throw new NotSupportedException();

    public Task ShutdownAsync(string host, bool reboot, TimeSpan delay, string? message, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class FakeUserDirectory : IUserDirectory
{
    public Func<string, IReadOnlyList<UserInfo>> OnSearch { get; set; } = _ => Array.Empty<UserInfo>();

    public Exception? SearchFails { get; set; }

    public ConcurrentQueue<string> SearchTexts { get; } = new();

    public Task<IReadOnlyList<UserInfo>> SearchAsync(string text, int max, CancellationToken ct)
    {
        SearchTexts.Enqueue(text);
        if (SearchFails is { } failure) throw failure;
        return Task.FromResult(OnSearch(text));
    }

    public Task<UserInfo?> GetBySamAccountNameAsync(string samAccountName, CancellationToken ct) => throw new NotSupportedException();

    public Task ResetPasswordAsync(string userDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken ct) => throw new NotSupportedException();

    public Task UnlockAsync(string userDn, CancellationToken ct) => throw new NotSupportedException();

    public Task SetEnabledAsync(string userDn, bool enabled, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(string userDn, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GroupInfo>> SearchGroupsAsync(string text, int max, CancellationToken ct) => throw new NotSupportedException();

    public Task AddToGroupAsync(string userDn, string groupDn, CancellationToken ct) => throw new NotSupportedException();

    public Task RemoveFromGroupAsync(string userDn, string groupDn, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class FakeDialogService : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;

    public ReasonResult ReasonAnswer { get; set; } = new(true, "плановые работы");

    public List<ConfirmRequest> ConfirmRequests { get; } = new();

    public List<ConfirmRequest> ReasonRequests { get; } = new();

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        lock (ConfirmRequests) ConfirmRequests.Add(request);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<ReasonResult> ConfirmWithReasonAsync(ConfirmRequest request)
    {
        lock (ReasonRequests) ReasonRequests.Add(request);
        return Task.FromResult(ReasonAnswer);
    }

    public Task<string?> PromptTextAsync(string title, string message, string? initial = null, bool multiline = false) => Task.FromResult<string?>(null);

    public Task<PasswordChoice?> PromptPasswordResetAsync(string userLabel) => Task.FromResult<PasswordChoice?>(null);

    public Task<GroupInfo?> PickGroupAsync(string title, Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>> search) => Task.FromResult<GroupInfo?>(null);

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

    public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
}

public sealed class FakeAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditRecord> _records = new();

    public IReadOnlyList<AuditRecord> Records => _records.ToArray();

    public Exception? ThrowOnWrite { get; set; }

    public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        if (ThrowOnWrite is { } failure) throw failure;
        _records.Enqueue(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int max, DateTime? sinceUtc = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>(Records.Reverse().Take(max).ToList());
}

public sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Settings { get; set; } = new();

    public AppSettings Load() => Settings;

    public void Save(AppSettings settings) => Settings = settings;
}

public sealed class FakeDomainConnection : IDomainConnection
{
    public string DomainName { get; init; } = "corp.local";
    public string? DomainController { get; init; } = "dc1.corp.local";
    public string AccountName { get; init; } = @"CORP\adm-ivanov";
    public bool UsesExplicitCredentials => false;
    public string AuthenticationKind => "Kerberos";
    public AccountCredential? Credential => null;

    public T RunImpersonated<T>(Func<T> action) => action();

    public void RunImpersonated(Action action) => action();

    public void Dispose()
    {
    }
}

public sealed class FakeCurrentConnection : ICurrentConnection
{
    public FakeCurrentConnection(IDomainConnection? current = null)
    {
        Current = current;
    }

    public IDomainConnection? Current { get; private set; }

    public event EventHandler? Changed;

    public void Set(IDomainConnection? connection)
    {
        Current = connection;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IDomainConnection Require() =>
        Current ?? throw new OperationFailedException("Не выполнен вход в домен.", OperationFailure.NotLoggedIn);

    public static FakeCurrentConnection LoggedIn() => new(new FakeDomainConnection());
}

/// <summary>Индекс-заглушка для сервисов, которым нужен только IComputerIndex.</summary>
public sealed class FakeComputerIndex : IComputerIndex
{
    private readonly object _gate = new();
    private readonly List<ComputerInfo> _computers = new();
    private readonly List<(Guid Id, bool? Online)> _setOnlineCalls = new();

    public IReadOnlyList<ComputerInfo> All
    {
        get
        {
            lock (_gate) return _computers.ToList();
        }
    }

    public IReadOnlyList<(Guid Id, bool? Online)> SetOnlineCalls
    {
        get
        {
            lock (_gate) return _setOnlineCalls.ToList();
        }
    }

    /// <summary>Вызывается при каждом SetOnline (из потока вызывающего).</summary>
    public Action<Guid, bool?>? OnSetOnline { get; set; }

    public bool IsLoading => false;
    public DateTime? LastSyncUtc => null;
    public string? LastError => null;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public FakeComputerIndex Add(params ComputerInfo[] computers)
    {
        lock (_gate) _computers.AddRange(computers);
        return this;
    }

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RefreshAsync(bool full, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<ComputerInfo> Search(string text, int max = 50) => Array.Empty<ComputerInfo>();

    public IReadOnlyList<ComputerInfo> GetByOu(string ouDn, bool includeNested)
    {
        lock (_gate)
        {
            return _computers
                .Where(c => includeNested ? Dn.IsDescendantOf(c.DistinguishedName, ouDn) : Dn.IsDirectChildOf(c.DistinguishedName, ouDn))
                .ToList();
        }
    }

    public ComputerInfo? Find(Guid id)
    {
        lock (_gate) return _computers.FirstOrDefault(c => c.Id == id);
    }

    public void SetOnline(Guid id, bool? online)
    {
        lock (_gate)
        {
            _setOnlineCalls.Add((id, online));
            var i = _computers.FindIndex(c => c.Id == id);
            if (i >= 0) _computers[i] = _computers[i] with { IsOnline = online };
        }

        OnSetOnline?.Invoke(id, online);
    }

    public void SetAvailability(
        Guid id, bool isChecking, bool? online = null, bool? rdp = null, bool? smb = null, bool? ssh = null,
        bool? icmp = null, DateTime? checkedAtUtc = null)
    {
        lock (_gate)
        {
            var i = _computers.FindIndex(c => c.Id == id);
            if (i < 0) return;
            var current = _computers[i];
            _computers[i] = current with
            {
                IsChecking = isChecking,
                IsOnline = isChecking ? current.IsOnline : online,
                RdpAvailable = isChecking ? current.RdpAvailable : rdp,
                SmbAvailable = isChecking ? current.SmbAvailable : smb,
                SshAvailable = isChecking ? current.SshAvailable : ssh,
                IcmpAvailable = isChecking ? current.IcmpAvailable : icmp,
                LastCheckedUtc = isChecking ? current.LastCheckedUtc : checkedAtUtc ?? current.LastCheckedUtc,
            };
        }
    }

    public void SetIpAddresses(Guid id, IReadOnlyList<string> addresses)
    {
        lock (_gate)
        {
            var i = _computers.FindIndex(c => c.Id == id);
            if (i >= 0) _computers[i] = _computers[i] with { IpAddresses = addresses };
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _computers.Clear();
        }
    }
}

/// <summary>Фейковый ICMP-пробер: по умолчанию отвечает true, настраивается как и <see cref="FakeHostProbe"/>.</summary>
public sealed class FakeIcmpProbe : IIcmpProbe
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public Func<string, bool> Online { get; set; } = _ => true;
    public HashSet<string> Throwing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> PingAsync(string hostOrIp, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Enqueue(hostOrIp);
        if (Throwing.Contains(hostOrIp)) throw new InvalidOperationException("Фейковый сбой ping.");
        return Task.FromResult(Online(hostOrIp));
    }
}

/// <summary>Фейковый обратный DNS: отдаёт имя по словарю IP→имя, иначе null (как «нет PTR-записи»).</summary>
public sealed class FakeReverseDnsResolver : IReverseDnsResolver
{
    public Dictionary<string, string?> Names { get; } = new();

    public Task<string?> ResolveAsync(string ipAddress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Names.GetValueOrDefault(ipAddress));
    }
}

/// <summary>Минимальный IServiceProvider для ActionRunner: действия и немного сервисов.</summary>
public sealed class SimpleServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    public SimpleServiceProvider Add<T>(T service) where T : notnull
    {
        _services[typeof(T)] = service;
        return this;
    }

    public SimpleServiceProvider AddActions<TTarget>(params IAction<TTarget>[] actions) where TTarget : IActionTarget
    {
        _services[typeof(IEnumerable<IAction<TTarget>>)] = actions.ToList();
        return this;
    }

    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
