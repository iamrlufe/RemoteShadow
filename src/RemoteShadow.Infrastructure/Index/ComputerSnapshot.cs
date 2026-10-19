using System.Collections;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Запись индекса: компьютер и заранее посчитанные строки для поиска (чтобы не приводить регистр на каждый запрос).
/// Меняется только статус «в сети»: <see cref="Info"/> подменяется целиком (<see cref="ComputerInfo"/> неизменяем),
/// поэтому читатель всегда видит целую запись, а смена статуса не требует пересборки снимка.
/// </summary>
internal sealed class ComputerEntry
{
    private ComputerInfo _info;

    public ComputerEntry(ComputerInfo info)
    {
        _info = info;
        NameLower = info.Name.ToLowerInvariant();
        DescriptionLower = string.IsNullOrEmpty(info.Description) ? null : info.Description.ToLowerInvariant();
        DnsLower = string.IsNullOrEmpty(info.DnsName) ? null : info.DnsName.ToLowerInvariant();
        ParentKey = Dn.Normalize(info.ParentDn);
    }

    public ComputerInfo Info => Volatile.Read(ref _info);
    public string NameLower { get; }
    public string? DescriptionLower { get; }
    public string? DnsLower { get; }

    /// <summary>Нормализованный DN родителя: ключ для списков подразделений.</summary>
    public string ParentKey { get; }

    /// <summary>Подставляет статус «в сети». Вызывать только под блокировкой записи индекса.</summary>
    public void SetOnline(bool? online) => Volatile.Write(ref _info, _info with { IsOnline = online });

    /// <summary>Подставляет результат многопротокольной проверки. Вызывать только под блокировкой записи индекса.</summary>
    public void SetAvailability(
        bool isChecking, bool? online, bool? rdp, bool? smb, bool? ssh, bool? icmp, DateTime? checkedAtUtc)
    {
        var current = _info;
        Volatile.Write(ref _info, current with
        {
            IsChecking = isChecking,
            IsOnline = isChecking ? current.IsOnline : online,
            RdpAvailable = isChecking ? current.RdpAvailable : rdp,
            SmbAvailable = isChecking ? current.SmbAvailable : smb,
            SshAvailable = isChecking ? current.SshAvailable : ssh,
            IcmpAvailable = isChecking ? current.IcmpAvailable : icmp,
            LastCheckedUtc = isChecking ? current.LastCheckedUtc : checkedAtUtc ?? current.LastCheckedUtc,
        });
    }

    /// <summary>Подставляет IP-адреса. Вызывать только под блокировкой записи индекса.</summary>
    public void SetIpAddresses(IReadOnlyList<string> addresses) => Volatile.Write(ref _info, _info with { IpAddresses = addresses });
}

/// <summary>Компьютеры одного подразделения: индексы в <see cref="ComputerSnapshot.Entries"/> по возрастанию, то есть по имени.</summary>
internal sealed record OuBucket(string Key, string[] Rdns, int[] Indices);

/// <summary>
/// Снимок индекса: состав записей, их порядок и подразделения не меняются. Читатели берут ссылку на снимок и работают
/// без блокировок; писатель собирает новый снимок и подменяет ссылку. Исключение — статус «в сети» внутри записи
/// (см. <see cref="ComputerEntry"/>).
/// </summary>
internal sealed class ComputerSnapshot
{
    public static readonly ComputerSnapshot Empty = Build(Array.Empty<ComputerEntry>());

    private readonly Dictionary<Guid, int> _indexById;
    private readonly Dictionary<string, OuBucket> _bucketsByKey;
    private readonly OuBucket[] _buckets;

    private ComputerSnapshot(
        ComputerEntry[] entries,
        Dictionary<Guid, int> indexById,
        Dictionary<string, OuBucket> bucketsByKey,
        OuBucket[] buckets)
    {
        Entries = entries;
        _indexById = indexById;
        _bucketsByKey = bucketsByKey;
        _buckets = buckets;
        All = new EntryList(entries);
    }

    /// <summary>Записи, отсортированные по имени (без учёта регистра).</summary>
    public ComputerEntry[] Entries { get; }

    public IReadOnlyList<ComputerInfo> All { get; }

    public static ComputerSnapshot Build(IEnumerable<ComputerEntry> source)
    {
        var entries = source.ToArray();
        Array.Sort(entries, CompareByName);

        var indexById = new Dictionary<Guid, int>(entries.Length);
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            indexById[entries[i].Info.Id] = i;

            var key = entries[i].ParentKey;
            if (!groups.TryGetValue(key, out var indices))
            {
                indices = new List<int>();
                groups[key] = indices;
            }

            indices.Add(i);
        }

        var bucketsByKey = new Dictionary<string, OuBucket>(groups.Count, StringComparer.Ordinal);
        var buckets = new OuBucket[groups.Count];
        var n = 0;
        foreach (var (key, indices) in groups)
        {
            var bucket = new OuBucket(key, Dn.SplitRdns(key).ToArray(), indices.ToArray());
            bucketsByKey[key] = bucket;
            buckets[n++] = bucket;
        }

        return new ComputerSnapshot(entries, indexById, bucketsByKey, buckets);
    }

    public bool TryGetIndex(Guid id, out int index) => _indexById.TryGetValue(id, out index);

    public ComputerInfo? Find(Guid id) => _indexById.TryGetValue(id, out var i) ? Entries[i].Info : null;

    /// <summary>Компьютеры подразделения; результат отсортирован по имени.</summary>
    public ComputerInfo[] GetByOu(string ouDn, bool includeNested)
    {
        var key = Dn.Normalize(ouDn);
        if (key.Length == 0) return Array.Empty<ComputerInfo>();

        if (!includeNested)
        {
            return _bucketsByKey.TryGetValue(key, out var direct)
                ? direct.Indices.Select(i => Entries[i].Info).ToArray()
                : Array.Empty<ComputerInfo>();
        }

        var ancestor = Dn.SplitRdns(key);
        var indices = new List<int>();
        foreach (var bucket in _buckets)
        {
            if (bucket.Key == key || IsInside(bucket.Rdns, ancestor)) indices.AddRange(bucket.Indices);
        }

        // Индексы идут в порядке имён, поэтому сортировка чисел даёт сортировку по имени.
        indices.Sort();
        return indices.Select(i => Entries[i].Info).ToArray();
    }

    // То же правило, что Dn.IsDescendantOf: больше компонентов, и хвост совпадает.
    private static bool IsInside(string[] rdns, IReadOnlyList<string> ancestor)
    {
        if (ancestor.Count == 0 || rdns.Length <= ancestor.Count) return false;

        for (var i = 1; i <= ancestor.Count; i++)
        {
            if (rdns[^i] != ancestor[^i]) return false;
        }

        return true;
    }

    private static int CompareByName(ComputerEntry a, ComputerEntry b)
    {
        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Info.Name, b.Info.Name);
        return byName != 0 ? byName : string.CompareOrdinal(a.Info.DistinguishedName, b.Info.DistinguishedName);
    }

    /// <summary>Список компьютеров поверх массива записей: без копирования.</summary>
    private sealed class EntryList(ComputerEntry[] entries) : IReadOnlyList<ComputerInfo>
    {
        public int Count => entries.Length;

        public ComputerInfo this[int index] => entries[index].Info;

        public IEnumerator<ComputerInfo> GetEnumerator()
        {
            foreach (var entry in entries) yield return entry.Info;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
