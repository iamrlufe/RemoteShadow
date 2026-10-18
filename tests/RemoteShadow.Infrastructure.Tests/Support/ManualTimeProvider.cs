namespace RemoteShadow.Infrastructure.Tests;

/// <summary>Время, которым управляет тест: стоит на месте, пока его не продвинут. Таймеры срабатывают при <see cref="Advance"/>.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>Сдвигает время вперёд, по пути вызывая сработавшие таймеры в порядке их срока.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers
                    .Where(t => t.Due is not null && t.Due <= target)
                    .OrderBy(t => t.Due)
                    .FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                if (next.Due > _now) _now = next.Due!.Value;
            }

            next.Fire();
        }
    }

    /// <summary>Сколько таймеров создано и ещё не освобождено (в том числе одноразовых, уже сработавших, но не освобождённых).</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate) return _timers.Count;
        }
    }

    internal void Remove(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    internal DateTimeOffset Now
    {
        get
        {
            lock (_gate) return _now;
        }
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.Now + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period > TimeSpan.Zero ? Due + _period : null;
            _callback(_state);
        }

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
