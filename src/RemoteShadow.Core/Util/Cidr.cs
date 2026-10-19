using System.Net;

namespace RemoteShadow.Core.Util;

/// <summary>
/// Диапазон адресов IPv4, посчитанный из CIDR (например, 10.0.1.0/24). Адрес в записи не обязан быть
/// выровнен по границе сети — он приводится к адресу сети маской. Для /31 (point-to-point, RFC 3021) и /32
/// (один адрес) сеть и broadcast не исключаются из «пригодных для использования» адресов.
/// </summary>
public readonly struct IPv4Range : IEquatable<IPv4Range>
{
    internal IPv4Range(uint network, uint broadcast, int prefixLength)
    {
        Network = network;
        Broadcast = broadcast;
        PrefixLength = prefixLength;
    }

    /// <summary>Адрес сети как 32-битное число, порядок байт от старшего октета к младшему.</summary>
    public uint Network { get; }

    /// <summary>Широковещательный адрес (для /31 и /32 совпадает со старшим адресом диапазона).</summary>
    public uint Broadcast { get; }

    public int PrefixLength { get; }

    public IPAddress NetworkAddress => ToAddress(Network);
    public IPAddress BroadcastAddress => ToAddress(Broadcast);

    /// <summary>Всего адресов в диапазоне, включая сеть и broadcast.</summary>
    public long TotalAddresses => (long)Broadcast - Network + 1;

    /// <summary>
    /// Адресов, пригодных для опроса/назначения узлам: для /31 и /32 — все адреса диапазона,
    /// для остальных — без сети и broadcast.
    /// </summary>
    public long UsableCount => PrefixLength >= 31 ? TotalAddresses : Math.Max(0, TotalAddresses - 2);

    public IPAddress? FirstUsable => UsableCount <= 0 ? null : ToAddress(PrefixLength >= 31 ? Network : Network + 1);

    public IPAddress? LastUsable => UsableCount <= 0 ? null : ToAddress(PrefixLength >= 31 ? Broadcast : Broadcast - 1);

    /// <summary>Все адреса диапазона, включая сеть и broadcast, по возрастанию.</summary>
    public IEnumerable<IPAddress> AllAddresses()
    {
        for (var value = Network; ; value++)
        {
            yield return ToAddress(value);
            if (value == Broadcast) yield break; // избегаем переполнения uint на Broadcast == uint.MaxValue (0.0.0.0/0)
        }
    }

    /// <summary>Адреса, пригодные для опроса: для /31 и /32 — все, иначе без сети и broadcast.</summary>
    public IEnumerable<IPAddress> UsableAddresses()
    {
        if (PrefixLength >= 31) return AllAddresses();
        return UsableCount <= 0 ? Array.Empty<IPAddress>() : Range(Network + 1, Broadcast - 1);
    }

    public bool Contains(IPAddress address) =>
        TryToUInt32(address, out var value) && value >= Network && value <= Broadcast;

    private static IEnumerable<IPAddress> Range(uint from, uint to)
    {
        for (var value = from; value <= to; value++) yield return ToAddress(value);
    }

    internal static IPAddress ToAddress(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    });

    internal static bool TryToUInt32(IPAddress address, out uint value)
    {
        value = 0;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    public bool Equals(IPv4Range other) => Network == other.Network && Broadcast == other.Broadcast && PrefixLength == other.PrefixLength;

    public override bool Equals(object? obj) => obj is IPv4Range other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Network, Broadcast, PrefixLength);

    public override string ToString() => $"{NetworkAddress}/{PrefixLength}";
}

/// <summary>Парсинг и валидация CIDR-записей IPv4 (например, «10.0.1.0/24»).</summary>
public static class Cidr
{
    /// <summary>
    /// Разбирает строку вида «a.b.c.d/n». Адрес маскируется до адреса сети, поэтому «10.0.1.5/24» и «10.0.1.0/24»
    /// дают одинаковый результат. Возвращает false и причину в <paramref name="error"/> при любой некорректности:
    /// пустая строка, нет «/», не IPv4-адрес (включая IPv6), префикс вне [0,32], лишние символы.
    /// </summary>
    public static bool TryParse(string? cidr, out IPv4Range range, out string? error)
    {
        range = default;
        error = null;

        if (string.IsNullOrWhiteSpace(cidr))
        {
            error = "Подсеть не может быть пустой.";
            return false;
        }

        var text = cidr.Trim();
        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            error = $"Нет префикса (например, «/24») в «{text}».";
            return false;
        }

        var addressPart = text[..slash];
        var prefixPart = text[(slash + 1)..];

        if (!IsStrictIPv4(addressPart))
        {
            error = $"«{addressPart}» — не адрес IPv4.";
            return false;
        }

        if (!IPAddress.TryParse(addressPart, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            error = $"«{addressPart}» — не адрес IPv4.";
            return false;
        }

        // Только цифры: int.TryParse по текущей культуре принял бы «+24», « 24» и «24 ».
        if (prefixPart.Length is 0 or > 2
            || !prefixPart.All(char.IsAsciiDigit)
            || !int.TryParse(prefixPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefixLength)
            || prefixLength is < 0 or > 32)
        {
            error = $"Префикс должен быть числом от 0 до 32, получено «{prefixPart}».";
            return false;
        }

        if (!IPv4Range.TryToUInt32(address, out var raw))
        {
            error = $"«{addressPart}» — не адрес IPv4.";
            return false;
        }

        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var network = raw & mask;
        var broadcast = network | ~mask;

        range = new IPv4Range(network, broadcast, prefixLength);
        return true;
    }

    /// <summary>То же самое, что <see cref="TryParse"/>, но бросает <see cref="FormatException"/> при ошибке.</summary>
    public static IPv4Range Parse(string? cidr)
    {
        if (TryParse(cidr, out var range, out var error)) return range;
        throw new FormatException(error);
    }

    // IPAddress.TryParse сам по себе принимает сокращённые формы («10.1» → 10.0.0.1) и десятичные/восьмеричные варианты
    // в некоторых рантаймах; для подсети нужна только строгая форма из четырёх десятичных октетов.
    private static bool IsStrictIPv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4) return false;

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3) return false;
            if (!part.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(part, out var octet) || octet is < 0 or > 255) return false;
        }

        return true;
    }
}
