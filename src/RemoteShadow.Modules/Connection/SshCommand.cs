using System.Text.RegularExpressions;
using RemoteShadow.Core;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Modules;

/// <summary>Логин и адресат для ssh: чистые функции без запуска процессов.</summary>
public static partial class SshCommand
{
    // «\z», а не «$»: иначе логин с переводом строки в конце прошёл бы проверку.
    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}\z")]
    private static partial Regex LoginRegex();

    /// <summary>
    /// Простой логин: латиница, цифры, точка, подчёркивание и дефис, до 64 символов. Формат user@domain не допускается.
    /// Дефис в начале запрещён: «user@host» не должен превратиться в ключ ssh.
    /// </summary>
    public static bool IsValidLogin(string? login) =>
        login is not null && !login.StartsWith('-') && LoginRegex().IsMatch(login);

    /// <summary>Логин по умолчанию: имя админской учётной записи без домена («CORP\adm» и «adm@corp.local» → «adm»).</summary>
    public static string DefaultLogin(string? accountName) =>
        string.IsNullOrWhiteSpace(accountName) ? string.Empty : AccountNameParser.Parse(accountName).UserName;

    /// <summary>«login@host»; логин и имя компьютера проверяются.</summary>
    public static string Destination(string login, string host)
    {
        if (!IsValidLogin(login))
            throw new OperationFailedException("Недопустимый логин для SSH.", OperationFailure.InvalidInput);
        return $"{login}@{HostGuard.Require(host)}";
    }
}
