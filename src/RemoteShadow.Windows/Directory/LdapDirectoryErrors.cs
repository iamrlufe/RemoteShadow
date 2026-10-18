using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text.RegularExpressions;
using RemoteShadow.Core;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows;

/// <summary>Что делали с каталогом, когда произошла ошибка. От этого зависит формулировка.</summary>
internal enum DirectoryOperation
{
    Read,
    ResetPassword,

    /// <summary>Второй шаг сброса пароля: pwdLastSet=0 («сменить при следующем входе»).</summary>
    RequirePasswordChange,
    Unlock,
    SetEnabled,
    AddToGroup,
    RemoveFromGroup,
}

/// <param name="Operation">Операция.</param>
/// <param name="ObjectDn">DN объекта операции: пользователь (для групп — тот, кого добавляют или убирают).</param>
/// <param name="GroupDn">DN группы для AddToGroup/RemoveFromGroup.</param>
/// <param name="ChannelEncrypted">Шифруется ли канал (Sealing/SSL). Для сброса пароля важно.</param>
internal readonly record struct DirectoryOperationInfo(
    DirectoryOperation Operation,
    string? ObjectDn = null,
    string? GroupDn = null,
    bool ChannelEncrypted = true);

/// <summary>
/// Превращает ошибки LDAP при работе с каталогом в <see cref="OperationFailedException"/> с понятным русским текстом:
/// для нехватки прав — какого права не хватает и на каком объекте. Код LDAP — в скобках в конце. Пароли сюда не попадают.
/// </summary>
internal static partial class LdapDirectoryErrors
{
    // Коды результата LDAP (RFC 4511) и клиентские коды wldap32.
    internal const int TimeLimitExceeded = 3;
    internal const int StrongAuthRequired = 8;
    internal const int ConfidentialityRequired = 13;
    internal const int NoSuchAttribute = 16;
    internal const int ConstraintViolation = 19;
    internal const int AttributeOrValueExists = 20;
    internal const int NoSuchObject = 32;
    internal const int InvalidDnSyntax = 34;
    internal const int InvalidCredentials = 49;
    internal const int InsufficientAccessRights = 50;
    internal const int Unavailable = 52;
    internal const int UnwillingToPerform = 53;
    internal const int EntryAlreadyExists = 68;
    internal const int ServerDown = 81;
    internal const int Timeout = 85;
    internal const int ConnectError = 91;

    // Коды Win32 в начале диагностики AD: "0000052D: SvcErr: DSID-..., problem 5003 (WILL_NOT_PERFORM)".
    private const int Win32PasswordRestriction = 0x52D;

    // ERROR_GEN_FAILURE: так AD отвечает, когда unicodePwd пришёл по каналу без шифрования (проверяется в домене).
    private const int Win32GeneralFailure = 0x1F;
    private const int Win32MemberInAlias = 0x562;
    private const int Win32MemberNotInAlias = 0x561;

    [GeneratedRegex(@"^\s*([0-9A-Fa-f]{8}):", RegexOptions.CultureInvariant)]
    private static partial Regex Win32PrefixRegex();

    /// <summary>Сопоставление любого исключения. Уже понятные <see cref="OperationFailedException"/> возвращаются как есть.</summary>
    public static OperationFailedException Translate(Exception exception, DirectoryOperationInfo op) => exception switch
    {
        OperationFailedException ready => ready,
        LdapException ldap => FromResult(ldap.ErrorCode, ldap.ServerErrorMessage, ldap.Message, op, ldap),
        DirectoryOperationException { Response: { } response } =>
            FromResult((int)response.ResultCode, response.ErrorMessage, exception.Message, op, exception),
        TimeoutException => new OperationFailedException(
            "Контроллер домена не ответил вовремя. Повторите попытку позже.", OperationFailure.Timeout, exception),
        _ => new OperationFailedException(
            $"Не удалось выполнить операцию в каталоге: {Clean(exception.Message)}", OperationFailure.Other, exception),
    };

    /// <summary>Сопоставление по коду результата LDAP и тексту сервера. Чистая функция.</summary>
    public static OperationFailedException FromResult(
        int code,
        string? serverMessage,
        string? message,
        DirectoryOperationInfo op,
        Exception? inner = null)
    {
        var win32 = ExtractWin32Code(serverMessage);
        var suffix = Code(code, win32);

        switch (code)
        {
            case InsufficientAccessRights:
                return new OperationFailedException(AccessDeniedText(op) + " " + suffix, OperationFailure.AccessDenied, inner);

            case NoSuchObject:
                return new OperationFailedException(
                    $"Объект не найден в каталоге: {Describe(op.GroupDn is not null && op.ObjectDn is null ? op.GroupDn : op.ObjectDn)}. "
                    + "Возможно, его удалили или переместили: обновите список. " + suffix,
                    OperationFailure.NotFound,
                    inner);

            case ConstraintViolation when op.Operation == DirectoryOperation.ResetPassword:
                return PasswordPolicy(suffix, inner);

            case UnwillingToPerform when op.Operation == DirectoryOperation.ResetPassword:
                if (win32 == Win32PasswordRestriction) return PasswordPolicy(suffix, inner);
                if (!op.ChannelEncrypted || win32 == Win32GeneralFailure) return EncryptionRequired(suffix, inner);
                return new OperationFailedException(
                    "Контроллер домена отказался установить пароль. Чаще всего пароль не проходит политику "
                    + "(длина, сложность, повтор прежних паролей) или сброс пароля для этой учётной записи запрещён. "
                    + suffix,
                    OperationFailure.PolicyViolation,
                    inner);

            case ConstraintViolation or UnwillingToPerform when op.Operation == DirectoryOperation.RequirePasswordChange:
                return new OperationFailedException(
                    "Контроллер домена отклонил требование сменить пароль при следующем входе. Чаще всего у учётной записи включён "
                    + "признак «Срок действия пароля не ограничен»: снимите его или оставьте пароль без требования смены. "
                    + suffix,
                    OperationFailure.PolicyViolation,
                    inner);

            case ConfidentialityRequired or StrongAuthRequired when op.Operation == DirectoryOperation.ResetPassword:
                return EncryptionRequired(suffix, inner);

            case AttributeOrValueExists or EntryAlreadyExists when op.Operation == DirectoryOperation.AddToGroup:
                return AlreadyMember(op, suffix, inner);

            case NoSuchAttribute when op.Operation == DirectoryOperation.RemoveFromGroup:
                return NotMember(op, suffix, inner);

            case UnwillingToPerform or ConstraintViolation when op.Operation == DirectoryOperation.AddToGroup && win32 == Win32MemberInAlias:
                return AlreadyMember(op, suffix, inner);

            case UnwillingToPerform or ConstraintViolation when op.Operation == DirectoryOperation.RemoveFromGroup && win32 == Win32MemberNotInAlias:
                return NotMember(op, suffix, inner);

            case InvalidDnSyntax:
                return new OperationFailedException(
                    $"Недопустимое имя объекта каталога. {suffix}", OperationFailure.InvalidInput, inner);

            case InvalidCredentials:
                return new OperationFailedException(
                    "Контроллер домена отклонил учётные данные. Войдите в домен заново. " + suffix,
                    OperationFailure.AccessDenied,
                    inner);

            case TimeLimitExceeded or Timeout:
                return new OperationFailedException(
                    "Контроллер домена не ответил вовремя. Повторите попытку позже. " + suffix,
                    OperationFailure.Timeout,
                    inner);

            case ServerDown or ConnectError or Unavailable:
                return new OperationFailedException(
                    "Контроллер домена недоступен. Проверьте сеть или VPN и повторите попытку. " + suffix,
                    OperationFailure.Unreachable,
                    inner);

            default:
                return new OperationFailedException(
                    $"Контроллер домена вернул ошибку: {Clean(string.IsNullOrWhiteSpace(serverMessage) ? message : serverMessage)}. {suffix}",
                    OperationFailure.Other,
                    inner);
        }
    }

    /// <summary>Win32-код из начала диагностики AD ("0000052D: ..." → 0x52D) или null.</summary>
    public static int? ExtractWin32Code(string? serverMessage)
    {
        if (string.IsNullOrEmpty(serverMessage)) return null;
        var match = Win32PrefixRegex().Match(serverMessage);
        return match.Success ? int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }

    private static string AccessDeniedText(DirectoryOperationInfo op)
    {
        var user = Describe(op.ObjectDn);
        var group = Describe(op.GroupDn);

        return op.Operation switch
        {
            DirectoryOperation.ResetPassword =>
                $"Нет права на сброс пароля у пользователя {user}. Нужно право «Reset password» (сброс пароля) "
                + $"на этого пользователя или на его подразделение{Where(op.ObjectDn)}. Попросите администратора домена делегировать его.",

            DirectoryOperation.RequirePasswordChange =>
                $"Нет права записи свойства pwdLastSet (требование смены пароля при входе) у пользователя {user}. "
                + $"Нужно право «Write pwdLastSet» на этого пользователя или на его подразделение{Where(op.ObjectDn)}.",

            DirectoryOperation.Unlock =>
                $"Нет права на разблокировку пользователя {user}. Нужно право записи свойства lockoutTime "
                + $"(задача «Unlock account») на этого пользователя или на его подразделение{Where(op.ObjectDn)}.",

            DirectoryOperation.SetEnabled =>
                $"Нет права включать и отключать учётную запись пользователя {user}. Нужно право записи свойства userAccountControl "
                + $"на этого пользователя или на его подразделение{Where(op.ObjectDn)}.",

            DirectoryOperation.AddToGroup or DirectoryOperation.RemoveFromGroup =>
                $"Нет права изменять состав группы {group}. Нужно право записи свойства member (Write members) на эту группу "
                + "или менеджер группы с этим правом.",

            _ => $"Недостаточно прав для чтения данных каталога{(op.ObjectDn is null ? string.Empty : ": " + user)}. "
                 + "Нужно право чтения этого объекта.",
        };
    }

    private static OperationFailedException PasswordPolicy(string suffix, Exception? inner) =>
        new(
            "Пароль не прошёл политику домена: он слишком короткий, недостаточно сложный или уже использовался раньше. "
            + "Задайте другой пароль. " + suffix,
            OperationFailure.PolicyViolation,
            inner);

    private static OperationFailedException EncryptionRequired(string suffix, Exception? inner) =>
        new(
            "Пароль можно сбросить только по шифрованному соединению с контроллером домена, а оно сейчас не шифруется. "
            + "Войдите в домен заново; если ошибка повторится, укажите контроллер домена по полному имени. " + suffix,
            OperationFailure.PolicyViolation,
            inner);

    private static OperationFailedException AlreadyMember(DirectoryOperationInfo op, string suffix, Exception? inner) =>
        new(
            $"Пользователь {Describe(op.ObjectDn)} уже состоит в группе {Describe(op.GroupDn)}. {suffix}",
            OperationFailure.Other,
            inner);

    private static OperationFailedException NotMember(DirectoryOperationInfo op, string suffix, Exception? inner) =>
        new(
            $"Пользователь {Describe(op.ObjectDn)} не состоит в группе {Describe(op.GroupDn)}. {suffix}",
            OperationFailure.Other,
            inner);

    /// <summary>«Иванов И.» в кавычках; если DN неизвестен — «объект».</summary>
    private static string Describe(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return "«объект»";
        var name = Dn.GetName(dn);
        return $"«{(name.Length == 0 ? dn : name)}»";
    }

    /// <summary>", расположенное в «OU=Склад,DC=corp,DC=local»" или пустая строка.</summary>
    private static string Where(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return string.Empty;
        var parent = Dn.Parent(dn);
        return parent.Length == 0 ? string.Empty : $" «{parent}»";
    }

    private static string Code(int code, int? win32) =>
        win32 is null
            ? $"(LDAP {code.ToString(CultureInfo.InvariantCulture)})"
            : $"(LDAP {code.ToString(CultureInfo.InvariantCulture)}, {win32.Value:X8})";

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "причина неизвестна";
        var trimmed = text.Trim().TrimEnd('.');
        return trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed;
    }
}
