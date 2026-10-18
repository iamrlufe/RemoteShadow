using System.Net;
using System.Text.RegularExpressions;

namespace RemoteShadow.Core.Util;

/// <summary>
/// Проверка имени компьютера перед передачей во внешние программы (mstsc, ssh, explorer).
/// Имя, начинающееся с «-» или «/», превратилось бы в ключ командной строки.
/// </summary>
public static partial class HostNameValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9_]([A-Za-z0-9_\-]{0,61}[A-Za-z0-9_])?$")]
    private static partial Regex LabelRegex();

    public static bool IsValid(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;

        if (IPAddress.TryParse(host, out var ip))
        {
            // Только IPv4: у IPv6 в строке есть двоеточия и %, которые mstsc/ssh разбирают по-своему.
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                   && host.Count(c => c == '.') == 3;
        }

        var labels = host.TrimEnd('.').Split('.');
        return labels.All(l => LabelRegex().IsMatch(l));
    }

    /// <summary>Возвращает имя, если оно допустимо, иначе бросает <see cref="OperationFailedException"/>.</summary>
    public static string Require(string? host)
    {
        if (!IsValid(host))
            throw new OperationFailedException($"Недопустимое имя компьютера: «{host}».", OperationFailure.InvalidInput);
        return host!;
    }
}
