using System.DirectoryServices.Protocols;
using System.Net;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows;

/// <summary>
/// Создаёт LdapConnection с едиными настройками: Negotiate, подпись и шифрование (Signing/Sealing), LDAP v3,
/// без перехода по ссылкам. Все вызовы LdapConnection синхронные: вызывающий оборачивает их в Task.Run.
/// </summary>
internal static class LdapConnectionFactory
{
    public const int LdapPort = 389;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Подключение для уже выполненного входа. Выполняет Bind.</summary>
    public static LdapConnection CreateBound(IDomainConnection connection)
    {
        var server = string.IsNullOrWhiteSpace(connection.DomainController) ? connection.DomainName : connection.DomainController;
        var network = connection.Credential is null ? null : BuildNetworkCredential(connection.Credential);

        var ldap = Create(server, network, DefaultTimeout);
        try
        {
            ldap.Bind();
            return ldap;
        }
        catch
        {
            ldap.Dispose();
            throw;
        }
    }

    /// <summary>Подключение без Bind. <paramref name="credential"/> = null — учётные данные текущего пользователя Windows.</summary>
    public static LdapConnection Create(string server, NetworkCredential? credential, TimeSpan timeout)
    {
        var ldap = new LdapConnection(new LdapDirectoryIdentifier(server, LdapPort), credential, AuthType.Negotiate);
        try
        {
            var options = ldap.SessionOptions;
            options.ProtocolVersion = 3;
            options.Signing = true;
            options.Sealing = true;
            options.ReferralChasing = ReferralChasingOptions.None;
            ldap.Timeout = timeout;
            return ldap;
        }
        catch
        {
            ldap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Для UPN (ivanov@corp.local) домен остаётся пустым: SSPI сам делит имя по @. Для остальных — логин и домен отдельно.
    /// </summary>
    public static NetworkCredential BuildNetworkCredential(AccountCredential credential) =>
        credential.UserName.Contains('@')
            ? new NetworkCredential(credential.UserName, credential.Password)
            : new NetworkCredential(credential.UserName, credential.Password, credential.Domain);
}
