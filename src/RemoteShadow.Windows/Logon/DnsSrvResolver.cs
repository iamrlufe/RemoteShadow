using System.Runtime.InteropServices;
using RemoteShadow.Windows.Native;

namespace RemoteShadow.Windows;

/// <summary>Запись DNS SRV: куда обращаться за службой.</summary>
internal sealed record SrvRecord(string Target, ushort Priority, ushort Weight, ushort Port)
{
    /// <summary>
    /// Порядок обращения: меньше priority — раньше; при равном priority больше weight — раньше (RFC 2782 без случайного
    /// выбора, чтобы порядок был предсказуем). Пустые цели и «.» («служба недоступна») отбрасываются, дубликаты склеиваются.
    /// </summary>
    public static IReadOnlyList<SrvRecord> Order(IEnumerable<SrvRecord> records) =>
        records
            .Select(r => r with { Target = r.Target.Trim().TrimEnd('.') })
            .Where(r => r.Target.Length > 0)
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.Weight)
            .ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Поиск контроллеров домена через DNS SRV (DnsQuery_W из dnsapi).</summary>
internal static class DnsSrvResolver
{
    /// <summary>Контроллеры домена по _ldap._tcp.dc._msdcs.&lt;домен&gt;, по порядку обращения. Пустой список — не нашли.</summary>
    public static IReadOnlyList<SrvRecord> FindDomainControllers(string domain) =>
        Query("_ldap._tcp.dc._msdcs." + domain.Trim().TrimEnd('.'));

    public static IReadOnlyList<SrvRecord> Query(string name)
    {
        var status = NativeMethods.DnsQuery(name, NativeMethods.DnsTypeSrv, 0, IntPtr.Zero, out var list, IntPtr.Zero);
        if (status != 0 || list == IntPtr.Zero) return Array.Empty<SrvRecord>();

        try
        {
            return SrvRecord.Order(ReadSrvRecords(list));
        }
        finally
        {
            NativeMethods.DnsRecordListFree(list, NativeMethods.DnsFreeRecordList);
        }
    }

    /// <summary>
    /// Читает связный список DNS_RECORD. Раскладка: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved (всего
    /// 2 указателя + 16 байт), затем данные записи. Для SRV: pNameTarget, wPriority, wWeight, wPort, Pad.
    /// В ответе вместе с SRV приходят адресные записи (A, AAAA) — они пропускаются по типу.
    /// </summary>
    internal static List<SrvRecord> ReadSrvRecords(IntPtr list)
    {
        var result = new List<SrvRecord>();
        var typeOffset = IntPtr.Size * 2;
        var dataOffset = IntPtr.Size * 2 + 16;

        for (var record = list; record != IntPtr.Zero; record = Marshal.ReadIntPtr(record))
        {
            var type = unchecked((ushort)Marshal.ReadInt16(record, typeOffset));
            if (type != NativeMethods.DnsTypeSrv) continue;

            var data = IntPtr.Add(record, dataOffset);
            var target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data));
            if (string.IsNullOrWhiteSpace(target)) continue;

            result.Add(new SrvRecord(
                target,
                unchecked((ushort)Marshal.ReadInt16(data, IntPtr.Size)),
                unchecked((ushort)Marshal.ReadInt16(data, IntPtr.Size + 2)),
                unchecked((ushort)Marshal.ReadInt16(data, IntPtr.Size + 4))));
        }

        return result;
    }
}
