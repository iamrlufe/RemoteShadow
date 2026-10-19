using System.IO.Compression;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

/// <summary>
/// Производитель по OUI. Данные — публичный реестр IEEE MA-L (встроенный ресурс <c>oui.txt.gz</c>, строки «AABBCC&lt;TAB&gt;Название»).
/// Загружается один раз при первом обращении.
/// </summary>
public sealed class OuiDatabase : IOuiDatabase
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _map;

    public OuiDatabase() : this(LoadEmbedded) { }

    internal OuiDatabase(Func<IReadOnlyDictionary<string, string>> loader) => _map = new Lazy<IReadOnlyDictionary<string, string>>(loader);

    public string? Lookup(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        Span<char> prefix = stackalloc char[6];
        var n = 0;
        foreach (var c in mac)
        {
            if (!Uri.IsHexDigit(c)) continue;
            prefix[n++] = char.ToUpperInvariant(c);
            if (n == 6) break;
        }

        if (n < 6) return null;
        return _map.Value.TryGetValue(new string(prefix), out var vendor) ? vendor : null;
    }

    private static IReadOnlyDictionary<string, string> LoadEmbedded()
    {
        var map = new Dictionary<string, string>(41_000, StringComparer.Ordinal);
        try
        {
            var asm = typeof(OuiDatabase).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("oui.txt.gz", StringComparison.Ordinal));
            if (name is null) return map;
            using var raw = asm.GetManifestResourceStream(name)!;
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            while (reader.ReadLine() is { } line)
            {
                var tab = line.IndexOf('\t');
                if (tab == 6) map[line[..6]] = line[7..];
            }
        }
        catch
        {
            // Нет базы — производитель просто не определяется.
        }

        return map;
    }
}
