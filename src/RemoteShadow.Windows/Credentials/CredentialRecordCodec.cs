using System.Text;

namespace RemoteShadow.Windows;

/// <summary>
/// Формат записи в Диспетчере учётных данных Windows. Цель — «RemoteShadow:login».
/// UserName: «домен\логин» (логин может сам содержать «\» или «@», делим по первой «\»).
/// Comment: «v=1;domain=corp.local;dc=dc01.corp.local» — значения закодированы Uri.EscapeDataString,
/// поэтому «;» и «=» в них не бывает. CredentialBlob: пароль в UTF-16, если админ разрешил его запомнить.
/// </summary>
internal static class CredentialRecordCodec
{
    public const string TargetName = "RemoteShadow:login";

    /// <summary>CRED_MAX_STRING_LENGTH: предел длины Comment.</summary>
    public const int MaxCommentLength = 256;

    /// <summary>CRED_MAX_USERNAME_LENGTH.</summary>
    public const int MaxUserNameLength = 513;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE, байт.</summary>
    public const int MaxBlobSize = 2560;

    private const string Version = "v=1";

    /// <summary>"corp.local" + "ivanov" → "corp.local\ivanov". Без домена — просто логин.</summary>
    public static string BuildUserName(string? domain, string userName) =>
        string.IsNullOrEmpty(domain) ? userName : $"{domain}\\{userName}";

    /// <summary>"corp.local\ivanov" → ("corp.local", "ivanov"). Без «\» домена нет.</summary>
    public static (string? Domain, string UserName) ParseUserName(string value)
    {
        var slash = value.IndexOf('\\');
        return slash > 0 && slash < value.Length - 1
            ? (value[..slash], value[(slash + 1)..])
            : (null, value);
    }

    public static string BuildComment(string? domain, string? domainController)
    {
        var sb = new StringBuilder(Version);
        if (!string.IsNullOrEmpty(domain)) sb.Append(";domain=").Append(Uri.EscapeDataString(domain));
        if (!string.IsNullOrEmpty(domainController)) sb.Append(";dc=").Append(Uri.EscapeDataString(domainController));
        return sb.ToString();
    }

    /// <summary>Разбор Comment. Чужой или повреждённый комментарий даёт пустые значения, а не исключение.</summary>
    public static (string? Domain, string? DomainController) ParseComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return (null, null);

        string? domain = null;
        string? controller = null;
        foreach (var part in comment.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;

            var key = part[..eq].Trim();
            var value = Unescape(part[(eq + 1)..].Trim());
            if (value.Length == 0) continue;

            if (key == "domain") domain = value;
            else if (key == "dc") controller = value;
        }

        return (domain, controller);
    }

    /// <summary>Пароль в байты (UTF-16, как его хранит Диспетчер). Вызывающий обнуляет массив.</summary>
    public static byte[] EncodePassword(string password) => Encoding.Unicode.GetBytes(password);

    public static string DecodePassword(ReadOnlySpan<byte> blob) => Encoding.Unicode.GetString(blob);

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
