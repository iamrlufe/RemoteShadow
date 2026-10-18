using System.Collections.Concurrent;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

/// <summary>Последние результаты опроса сессий по компьютерам. Потокобезопасный; <see cref="Changed"/> приходит из любого потока.</summary>
public sealed class SessionCache : ISessionCache
{
    private readonly ConcurrentDictionary<Guid, SessionSnapshot> _snapshots = new();

    public event EventHandler? Changed;

    public SessionSnapshot? Get(Guid computerId) =>
        _snapshots.TryGetValue(computerId, out var snapshot) ? snapshot : null;

    public void Set(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots[snapshot.ComputerId] = snapshot;
        EventRaiser.Raise(Changed, this);
    }

    public IReadOnlyList<SessionSnapshot> GetAll() => _snapshots.Values.ToArray();

    public void Clear()
    {
        _snapshots.Clear();
        EventRaiser.Raise(Changed, this);
    }
}
