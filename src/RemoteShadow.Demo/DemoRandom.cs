namespace RemoteShadow.Demo;

/// <summary>
/// Маленький детерминированный генератор (SplitMix64). Свой, а не <see cref="Random"/>, чтобы данные
/// с одним seed были одинаковыми на любой версии .NET, и чтобы хеш имени не зависел от процесса.
/// </summary>
internal sealed class DemoRandom
{
    private ulong _state;

    public DemoRandom(int seed)
    {
        _state = unchecked((ulong)(uint)seed);
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Число в диапазоне [0, maxExclusive).</summary>
    public int Next(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)(NextUInt64() % (ulong)maxExclusive);
    }

    /// <summary>Число в диапазоне [min, maxExclusive).</summary>
    public int Next(int min, int maxExclusive)
    {
        if (maxExclusive <= min) return min;
        return min + Next(maxExclusive - min);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double probability) => NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>Перемешивание Фишера — Йетса.</summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public Guid NextGuid()
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), NextUInt64());
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), NextUInt64());
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>FNV-1a без учёта регистра. Не зависит от процесса, в отличие от <see cref="string.GetHashCode()"/>.</summary>
    public static ulong Hash(string text)
    {
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var c in text)
            {
                hash ^= char.ToUpperInvariant(c);
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }
}

/// <summary>Ожидание с учётом нулевой задержки и отмены.</summary>
internal static class DemoDelay
{
    public static Task Wait(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
            return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        return Task.Delay(delay, ct);
    }

    /// <summary>Детерминированная задержка из диапазона [min, max] по ключу (имени хоста).</summary>
    public static TimeSpan ByKey(string key, TimeSpan min, TimeSpan max)
    {
        if (max <= min) return min;
        var spanMs = (long)(max - min).TotalMilliseconds;
        var offset = (long)(DemoRandom.Hash(key) % (ulong)(spanMs + 1));
        return min + TimeSpan.FromMilliseconds(offset);
    }
}
