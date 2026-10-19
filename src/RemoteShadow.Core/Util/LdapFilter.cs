using System.Text;

namespace RemoteShadow.Core.Util;

public static class LdapFilter
{
    /// <summary>Экранирует значение для подстановки в LDAP-фильтр (RFC 4515). Обязательно для любого текста от пользователя.</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Время для фильтра по whenChanged в формате generalized time: 20260115103000.0Z.</summary>
    /// <remarks>
    /// Время без указания вида (<see cref="DateTimeKind.Unspecified"/>, например прочитанное из файла без «Z») считается UTC,
    /// как и задано контрактом: ToUniversalTime() приняло бы его за местное и сдвинуло на часовой пояс.
    /// </remarks>
    public static string GeneralizedTime(DateTime utc) =>
        (utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime())
            .ToString("yyyyMMddHHmmss'.0Z'", System.Globalization.CultureInfo.InvariantCulture);
}
