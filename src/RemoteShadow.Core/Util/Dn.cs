using System.Text;

namespace RemoteShadow.Core.Util;

/// <summary>Работа с distinguished name (RFC 4514): экранированные запятые, регистр, пробелы.</summary>
public static class Dn
{
    /// <summary>Делит DN на RDN по неэкранированным запятым: "CN=A\, B,OU=X,DC=corp" → ["CN=A\, B","OU=X","DC=corp"].</summary>
    public static IReadOnlyList<string> SplitRdns(string dn)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(dn)) return result;

        var sb = new StringBuilder();
        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];
            if (c == '\\' && i + 1 < dn.Length)
            {
                sb.Append(c).Append(dn[++i]);
            }
            else if (c == ',')
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        var last = sb.ToString().Trim();
        if (last.Length > 0) result.Add(last);
        return result;
    }

    /// <summary>DN родителя: "CN=PC1,OU=Склад,DC=corp" → "OU=Склад,DC=corp". Пустая строка, если родителя нет.</summary>
    public static string Parent(string dn)
    {
        var rdns = SplitRdns(dn);
        return rdns.Count <= 1 ? string.Empty : string.Join(",", rdns.Skip(1));
    }

    /// <summary>Значение первого RDN без экранирования: "OU=Ivanov\, A,DC=x" → "Ivanov, A".</summary>
    public static string GetName(string dn)
    {
        var rdns = SplitRdns(dn);
        if (rdns.Count == 0) return string.Empty;

        var first = rdns[0];
        var eq = first.IndexOf('=');
        var raw = eq >= 0 ? first[(eq + 1)..] : first;
        return Unescape(raw);
    }

    /// <summary>Нормализованный вид для сравнения: без пробелов вокруг запятых и знаков равенства, в нижнем регистре.</summary>
    public static string Normalize(string dn)
    {
        var parts = SplitRdns(dn).Select(rdn =>
        {
            var eq = rdn.IndexOf('=');
            return eq < 0 ? rdn.Trim() : rdn[..eq].Trim() + "=" + rdn[(eq + 1)..].Trim();
        });
        return string.Join(",", parts).ToLowerInvariant();
    }

    public static bool AreEqual(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) == Normalize(b);

    /// <summary>true, если <paramref name="dn"/> лежит строго внутри <paramref name="ancestorDn"/> на любой глубине.</summary>
    public static bool IsDescendantOf(string dn, string ancestorDn)
    {
        var a = SplitRdns(Normalize(dn));
        var b = SplitRdns(Normalize(ancestorDn));
        if (b.Count == 0 || a.Count <= b.Count) return false;

        for (var i = 1; i <= b.Count; i++)
        {
            if (a[^i] != b[^i]) return false;
        }

        return true;
    }

    /// <summary>true, если родитель <paramref name="dn"/> — ровно <paramref name="parentDn"/>.</summary>
    public static bool IsDirectChildOf(string dn, string parentDn) => AreEqual(Parent(dn), parentDn);

    /// <summary>Экранирует значение для подстановки в RDN: запятые, плюс, кавычки, скобки угла, точка с запятой, обратная косая.</summary>
    public static string EscapeValue(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            // NUL, CR, LF и прочие управляющие символы допустимы только как hexpair (RFC 4514): «\00», «\0A».
            if (char.IsControl(c) && c < 0x80)
            {
                sb.Append('\\').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            var needs = c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '='
                        || (i == 0 && (c is '#' or ' '))
                        || (i == value.Length - 1 && c == ' ');
            if (needs) sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>"corp.local" → "DC=corp,DC=local".</summary>
    public static string FromDnsDomain(string dnsDomain) =>
        string.Join(",", dnsDomain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(label => "DC=" + label));

    /// <summary>"OU=A,DC=corp,DC=local" → "corp.local" (берутся только компоненты DC=).</summary>
    public static string ToDnsDomain(string dn) =>
        string.Join(".", SplitRdns(dn)
            .Where(r => r.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(r => r[3..]));

    private static string Unescape(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;

        var bytes = new List<byte>();
        var sb = new StringBuilder();

        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length)
            {
                FlushBytes();
                sb.Append(c);
                continue;
            }

            var next = raw[i + 1];
            if (i + 2 < raw.Length && Uri.IsHexDigit(next) && Uri.IsHexDigit(raw[i + 2]))
            {
                bytes.Add(Convert.ToByte(raw.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                FlushBytes();
                sb.Append(next);
                i++;
            }
        }

        FlushBytes();
        return sb.ToString();
    }
}
