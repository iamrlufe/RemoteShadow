using RemoteShadow.Core.Services;

namespace RemoteShadow.Core.Models;

/// <summary>Учётные данные. Пароль живёт только в памяти. Никогда не логировать и не писать на диск.</summary>
public sealed class AccountCredential
{
    public AccountCredential(string domain, string userName, string password)
    {
        Domain = domain;
        UserName = userName;
        Password = password;
    }

    public string Domain { get; }
    public string UserName { get; }
    public string Password { get; }

    /// <summary>CORP\adm-ivanov</summary>
    public string DisplayName => string.IsNullOrEmpty(Domain) ? UserName : $"{Domain}\\{UserName}";

    public override string ToString() => DisplayName;
}

public enum LogonMode
{
    /// <summary>Текущий пользователь Windows, Kerberos без пароля.</summary>
    CurrentUser,

    /// <summary>Явные учётные данные: домен, логин, пароль.</summary>
    Explicit,
}

/// <param name="Domain">DNS-имя домена, например corp.local.</param>
/// <param name="DomainController">Контроллер домена; null — найти автоматически через DNS SRV.</param>
/// <param name="UserName">Логин: ivanov, CORP\ivanov или ivanov@corp.local. Для CurrentUser не нужен.</param>
public sealed record LogonRequest(
    LogonMode Mode,
    string? Domain,
    string? DomainController,
    string? UserName,
    string? Password)
{
    // Сгенерированный ToString записи выводит все свойства, в том числе пароль: он не должен попасть в журнал.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Mode = ").Append(Mode)
            .Append(", Domain = ").Append(Domain)
            .Append(", DomainController = ").Append(DomainController)
            .Append(", UserName = ").Append(UserName)
            .Append(", Password = ").Append(Password is null ? "null" : "***");
        return true;
    }
}

public enum LogonError
{
    None,
    InvalidCredentials,
    AccountLocked,
    AccountDisabled,
    PasswordExpired,
    DomainControllerUnreachable,
    DnsFailure,
    DomainNotSpecified,
    Unknown,
}

public sealed record LogonResult(
    bool Success,
    IDomainConnection? Connection,
    LogonError Error,
    string? Message,
    bool IsPrivilegedAccount = false)
{
    public static LogonResult Ok(IDomainConnection connection, bool isPrivileged) =>
        new(true, connection, LogonError.None, null, isPrivileged);

    public static LogonResult Fail(LogonError error, string message) =>
        new(false, null, error, message);
}

/// <summary>Что известно о компьютере, на котором запущена программа.</summary>
/// <param name="DomainName">DNS-имя домена, если компьютер в домене.</param>
public sealed record DomainEnvironmentInfo(
    bool IsDomainJoined,
    string? DomainName,
    string ComputerName,
    string CurrentUser);

/// <summary>Сохранённые данные входа. Пароль есть только если админ явно отметил «запомнить».</summary>
public sealed record StoredLogon(
    string Domain,
    string? DomainController,
    string UserName,
    string? Password)
{
    // Сгенерированный ToString записи вывел бы сохранённый пароль.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Domain = ").Append(Domain)
            .Append(", DomainController = ").Append(DomainController)
            .Append(", UserName = ").Append(UserName)
            .Append(", Password = ").Append(Password is null ? "null" : "***");
        return true;
    }
}
