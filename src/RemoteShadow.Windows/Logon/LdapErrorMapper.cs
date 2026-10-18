using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows;

/// <summary>Ошибка входа в домен: категория для интерфейса и готовый русский текст.</summary>
internal readonly record struct LogonFailure(LogonError Error, string Message);

/// <summary>
/// Превращает ошибки LDAP и сокетов при пробном подключении в <see cref="LogonFailure"/>.
/// Текст всегда по-русски, с подсказкой, что сделать; технический код — в скобках в конце.
/// </summary>
internal static partial class LdapErrorMapper
{
    // Коды результата LDAP и клиентские коды wldap32.
    internal const int StrongAuthRequired = 8;
    internal const int ConfidentialityRequired = 13;
    internal const int InvalidCredentials = 49;
    internal const int Unavailable = 52;
    internal const int ServerDown = 81;
    internal const int LocalError = 82;
    internal const int Timeout = 85;
    internal const int ConnectError = 91;

    // Подкод Active Directory из ServerErrorMessage: "... AcceptSecurityContext error, data 52e, v4563".
    [GeneratedRegex(@"\bdata\s+([0-9a-fA-F]{3,4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex AdSubCodeRegex();

    /// <summary>
    /// Разбирает исключение, возникшее при подключении к контроллеру домена <paramref name="host"/>.
    /// <paramref name="currentUser"/> — вход текущим пользователем Windows: пароль не вводился, поэтому
    /// «неверный логин или пароль» заменяется подсказкой про учётную запись Windows.
    /// </summary>
    public static LogonFailure Map(Exception exception, string? host = null, bool currentUser = false) => exception switch
    {
        LdapException ldap => MapLdap(ldap.ErrorCode, ldap.ServerErrorMessage, ldap.Message, host, currentUser),
        DirectoryOperationException { Response: { } response } =>
            MapLdap((int)response.ResultCode, response.ErrorMessage, exception.Message, host, currentUser),
        SocketException socket => MapSocket(socket.SocketErrorCode, host),
        TimeoutException => Unreachable(host, "Контроллер домена не ответил вовремя.", null),
        _ => new LogonFailure(
            LogonError.Unknown,
            $"Не удалось выполнить вход: {Clean(exception.Message)}"),
    };

    /// <summary>Разбор по числовому коду LDAP и тексту сервера. Чистая функция.</summary>
    public static LogonFailure MapLdap(int errorCode, string? serverMessage, string? message, string? host = null, bool currentUser = false)
    {
        var sub = ExtractAdSubCode(serverMessage);

        switch (errorCode)
        {
            case InvalidCredentials:
                return MapInvalidCredentials(sub, currentUser);

            case ServerDown:
            case ConnectError:
            case Unavailable:
                return Unreachable(host, "Контроллер домена не отвечает.", errorCode);

            case Timeout:
                return Unreachable(host, "Контроллер домена не ответил вовремя.", errorCode);

            case LocalError:
                return new LogonFailure(
                    LogonError.Unknown,
                    "Не удалось согласовать проверку подлинности с контроллером домена. "
                    + "Проверьте, что время на этом компьютере отличается от времени контроллера не более чем на 5 минут, "
                    + "а имя контроллера указано полностью (dc01.corp.local), а не IP-адресом. "
                    + Code(errorCode, null));

            case StrongAuthRequired:
            case ConfidentialityRequired:
                return new LogonFailure(
                    LogonError.Unknown,
                    "Контроллер домена требует подписанного и шифрованного соединения, а согласовать его не удалось. "
                    + "Попробуйте указать контроллер домена по полному имени. " + Code(errorCode, null));

            default:
                return new LogonFailure(
                    LogonError.Unknown,
                    $"Не удалось подключиться к домену: {Clean(string.IsNullOrWhiteSpace(message) ? serverMessage : message)} "
                    + Code(errorCode, sub));
        }
    }

    /// <summary>Подкод AD из ServerErrorMessage ("data 52e" → "52e") в нижнем регистре или null.</summary>
    public static string? ExtractAdSubCode(string? serverMessage)
    {
        if (string.IsNullOrEmpty(serverMessage)) return null;
        var match = AdSubCodeRegex().Match(serverMessage);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static LogonFailure MapInvalidCredentials(string? sub, bool currentUser) => sub switch
    {
        "775" => new LogonFailure(
            LogonError.AccountLocked,
            "Учётная запись заблокирована: слишком много неверных паролей. "
            + "Дождитесь автоматической разблокировки или попросите администратора домена разблокировать её. "
            + Code(InvalidCredentials, sub)),

        "533" => new LogonFailure(
            LogonError.AccountDisabled,
            "Учётная запись отключена. Обратитесь к администратору домена. " + Code(InvalidCredentials, sub)),

        "701" => new LogonFailure(
            LogonError.AccountDisabled,
            "Срок действия учётной записи истёк. Обратитесь к администратору домена. " + Code(InvalidCredentials, sub)),

        "532" => new LogonFailure(
            LogonError.PasswordExpired,
            "Срок действия пароля истёк. Смените пароль (например, на доменном компьютере: Ctrl+Alt+Del, «Изменить пароль») "
            + "и повторите вход. " + Code(InvalidCredentials, sub)),

        "773" => new LogonFailure(
            LogonError.PasswordExpired,
            "Нужно сменить пароль при первом входе. Смените пароль (например, на доменном компьютере: Ctrl+Alt+Del, «Изменить пароль») "
            + "и повторите вход. " + Code(InvalidCredentials, sub)),

        "525" when !currentUser => new LogonFailure(
            LogonError.InvalidCredentials,
            "Пользователь не найден в домене. Проверьте логин и домен. " + Code(InvalidCredentials, sub)),

        "530" => new LogonFailure(
            LogonError.Unknown,
            "Этой учётной записи запрещён вход в это время суток. Обратитесь к администратору домена. "
            + Code(InvalidCredentials, sub)),

        "531" => new LogonFailure(
            LogonError.Unknown,
            "Этой учётной записи запрещён вход с этого компьютера. Обратитесь к администратору домена. "
            + Code(InvalidCredentials, sub)),

        _ when currentUser => new LogonFailure(
            LogonError.InvalidCredentials,
            "Контроллер домена не принял текущую учётную запись Windows: она не доменная или не распознана в этом домене. "
            + "Выберите вход другой учётной записью и введите доменный логин и пароль. " + Code(InvalidCredentials, sub)),

        _ => new LogonFailure(
            LogonError.InvalidCredentials,
            "Неверный логин или пароль. Проверьте раскладку клавиатуры, Caps Lock и правильность домена. "
            + Code(InvalidCredentials, sub)),
    };

    private static LogonFailure MapSocket(SocketError error, string? host)
    {
        var name = string.IsNullOrWhiteSpace(host) ? "домена" : $"«{host}»";
        switch (error)
        {
            case SocketError.HostNotFound:
            case SocketError.NoData:
            case SocketError.TryAgain:
            case SocketError.NoRecovery:
                return new LogonFailure(
                    LogonError.DnsFailure,
                    $"Не удалось найти контроллер {name} через DNS. Проверьте, что DNS-сервер этого компьютера "
                    + "разрешает имена домена (он должен указывать на контроллер домена), либо подключитесь к VPN. "
                    + $"(DNS, {(int)error})");

            case SocketError.ConnectionRefused:
                return new LogonFailure(
                    LogonError.DomainControllerUnreachable,
                    $"Контроллер {name} отказал в подключении к порту 389 (LDAP). "
                    + "Проверьте, что указан именно контроллер домена и что брандмауэр не блокирует порт 389. "
                    + $"(сеть, {(int)error})");

            default:
                return Unreachable(host, "Не удалось подключиться к контроллеру домена по сети.", (int)error);
        }
    }

    private static LogonFailure Unreachable(string? host, string what, int? code)
    {
        var target = string.IsNullOrWhiteSpace(host) ? string.Empty : $" «{host}»";
        return new LogonFailure(
            LogonError.DomainControllerUnreachable,
            $"{what} Контроллер{target} недоступен: проверьте, что он включён, что порт 389 (LDAP) не закрыт брандмауэром "
            + "и что подключён VPN, если вы вне офисной сети."
            + (code is null ? string.Empty : " " + Code(code.Value, null)));
    }

    private static string Code(int ldapCode, string? sub) =>
        sub is null
            ? $"(LDAP {ldapCode.ToString(CultureInfo.InvariantCulture)})"
            : $"(LDAP {ldapCode.ToString(CultureInfo.InvariantCulture)}, {sub})";

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "причина неизвестна";
        var trimmed = text.Trim().TrimEnd('.');
        return trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed;
    }
}
