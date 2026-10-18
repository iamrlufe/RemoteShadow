using System.Globalization;

namespace RemoteShadow.Windows;

/// <summary>Разбор значений атрибутов AD: FILETIME (lastLogonTimestamp, pwdLastSet) и generalized time (whenChanged).</summary>
internal static class LdapValueParser
{
    // 1601-01-01 в тиках DateTime.
    private const long FileTimeEpochTicks = 504_911_232_000_000_000;

    /// <summary>
    /// FILETIME в десятичной строке (100-нс интервалы с 1601-01-01 UTC) в UTC.
    /// null для пустого значения, 0 («никогда») и 0x7FFFFFFFFFFFFFFF («бессрочно»).
    /// </summary>
    public static DateTime? ParseFileTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileTime)
            ? FromFileTime(fileTime)
            : null;
    }

    public static DateTime? FromFileTime(long fileTime)
    {
        if (fileTime <= 0 || fileTime == long.MaxValue) return null;
        if (fileTime > DateTime.MaxValue.Ticks - FileTimeEpochTicks) return null;
        return new DateTime(fileTime + FileTimeEpochTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Generalized time (RFC 4517): 20260115103000.0Z. Допускаются дробные секунды и смещение вместо Z
    /// (+0300, -05); без суффикса время считается UTC. Результат — UTC. Неразборчивое значение даёт null.
    /// </summary>
    public static DateTime? ParseGeneralizedTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.Length < 14) return null;

        for (var i = 0; i < 14; i++)
        {
            if (!char.IsAsciiDigit(s[i])) return null;
        }

        var position = 14;
        long fractionTicks = 0;
        if (position < s.Length && (s[position] == '.' || s[position] == ','))
        {
            position++;
            var scale = 1_000_000L; // 0.1 с = 1 000 000 тиков
            while (position < s.Length && char.IsAsciiDigit(s[position]))
            {
                fractionTicks += (s[position] - '0') * scale;
                scale /= 10;
                position++;
            }
        }

        var offset = TimeSpan.Zero;
        if (position < s.Length)
        {
            var marker = s[position];
            if (marker is '+' or '-')
            {
                var digits = s[(position + 1)..];
                if (digits.Length is not (2 or 4) || !digits.All(char.IsAsciiDigit)) return null;
                var hours = int.Parse(digits[..2], CultureInfo.InvariantCulture);
                var minutes = digits.Length == 4 ? int.Parse(digits[2..], CultureInfo.InvariantCulture) : 0;
                offset = new TimeSpan(hours, minutes, 0);
                if (marker == '-') offset = -offset;
            }
            else if (marker != 'Z' || position != s.Length - 1)
            {
                return null;
            }
        }

        try
        {
            var local = new DateTime(
                int.Parse(s[0..4], CultureInfo.InvariantCulture),
                int.Parse(s[4..6], CultureInfo.InvariantCulture),
                int.Parse(s[6..8], CultureInfo.InvariantCulture),
                int.Parse(s[8..10], CultureInfo.InvariantCulture),
                int.Parse(s[10..12], CultureInfo.InvariantCulture),
                int.Parse(s[12..14], CultureInfo.InvariantCulture),
                DateTimeKind.Utc);
            return local.AddTicks(fractionTicks) - offset;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Целое из строки атрибута (userAccountControl, msDS-User-Account-Control-Computed). null, если не число.</summary>
    public static int? ParseInt(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? unchecked((int)number)
            : null;
}
