using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Security.Cryptography;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows;

/// <summary>
/// Пользователи и группы AD через LDAP. Изменения (пароль, блокировка, группы) выполняются от имени текущего подключения;
/// нехватка прав превращается в понятную ошибку с названием права и объекта (см. <see cref="LdapDirectoryErrors"/>).
/// </summary>
public sealed class LdapUserDirectory : IUserDirectory
{
    private readonly ICurrentConnection _current;

    public LdapUserDirectory(ICurrentConnection current)
    {
        _current = current;
    }

    public async Task<IReadOnlyList<UserInfo>> SearchAsync(string text, int max, CancellationToken ct)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0 || max <= 0) return Array.Empty<UserInfo>();

        return await WithSessionAsync(
            new DirectoryOperationInfo(DirectoryOperation.Read),
            async session =>
            {
                var rootDn = await session.RunAsync(LdapRootDse.ReadDefaultNamingContext, ct).ConfigureAwait(false);
                var request = new SearchRequest(rootDn, LdapFilters.UserSearch(text), SearchScope.Subtree, DirectoryMappers.UserAttributes);
                var entries = await session.RunAsync(ldap => PagedSearch.ReadUpTo(ldap, request, max), ct).ConfigureAwait(false);
                return (IReadOnlyList<UserInfo>)MapUsers(entries);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<UserInfo?> GetBySamAccountNameAsync(string samAccountName, CancellationToken ct)
    {
        samAccountName = (samAccountName ?? string.Empty).Trim();
        if (samAccountName.Length == 0) return null;

        return await WithSessionAsync(
            new DirectoryOperationInfo(DirectoryOperation.Read),
            async session =>
            {
                var rootDn = await session.RunAsync(LdapRootDse.ReadDefaultNamingContext, ct).ConfigureAwait(false);
                var request = new SearchRequest(rootDn, LdapFilters.UserBySam(samAccountName), SearchScope.Subtree, DirectoryMappers.UserAttributes);
                var entries = await session.RunAsync(ldap => PagedSearch.ReadUpTo(ldap, request, 1), ct).ConfigureAwait(false);
                return MapUsers(entries).FirstOrDefault();
            },
            ct).ConfigureAwait(false);
    }

    public async Task ResetPasswordAsync(string userDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken ct)
    {
        RequireDn(userDn);
        if (string.IsNullOrEmpty(newPassword))
            throw new OperationFailedException("Новый пароль не задан.", OperationFailure.InvalidInput);

        await ModifyAsync(
            new DirectoryOperationInfo(DirectoryOperation.ResetPassword, userDn),
            ldap =>
            {
                // unicodePwd принимается только по шифрованному каналу; без шифрования пароль ушёл бы открытым текстом.
                if (!ldap.SessionOptions.Sealing && !ldap.SessionOptions.SecureSocketLayer)
                {
                    throw new OperationFailedException(
                        "Пароль нельзя сбросить: соединение с контроллером домена не шифруется. "
                        + "Войдите в домен заново; если ошибка повторится, укажите контроллер домена по полному имени.",
                        OperationFailure.PolicyViolation);
                }

                var encoded = UnicodePwdEncoder.Encode(newPassword);
                try
                {
                    ldap.SendRequest(new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, "unicodePwd", new object[] { encoded }));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }

                if (!mustChangeAtNextLogon) return;

                try
                {
                    ldap.SendRequest(new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, "pwdLastSet", "0"));
                }
                catch (Exception ex)
                {
                    var reason = LdapDirectoryErrors.Translate(ex, new DirectoryOperationInfo(DirectoryOperation.RequirePasswordChange, userDn));
                    throw new OperationFailedException(
                        "Пароль сброшен, но потребовать смену пароля при следующем входе не удалось. " + reason.Message,
                        reason.Failure,
                        ex);
                }
            },
            ct).ConfigureAwait(false);
    }

    public async Task UnlockAsync(string userDn, CancellationToken ct)
    {
        RequireDn(userDn);
        await ModifyAsync(
            new DirectoryOperationInfo(DirectoryOperation.Unlock, userDn),
            ldap => ldap.SendRequest(new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, "lockoutTime", "0")),
            ct).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(string userDn, bool enabled, CancellationToken ct)
    {
        RequireDn(userDn);
        await ModifyAsync(
            new DirectoryOperationInfo(DirectoryOperation.SetEnabled, userDn),
            ldap =>
            {
                var read = new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, "userAccountControl");
                var response = (SearchResponse)ldap.SendRequest(read);
                var current = response.Entries.Count > 0
                    ? LdapValueParser.ParseInt(new SearchEntryAttributes(response.Entries[0]).GetString("userAccountControl"))
                    : null;

                if (current is null)
                {
                    throw new OperationFailedException(
                        $"Не удалось прочитать флаги учётной записи «{Dn.GetName(userDn)}» (userAccountControl). Возможно, нет права чтения.",
                        OperationFailure.AccessDenied);
                }

                var updated = UserAccountControl.WithEnabled(current.Value, enabled);
                if (updated == current.Value) return;

                ldap.SendRequest(new ModifyRequest(
                    userDn,
                    DirectoryAttributeOperation.Replace,
                    "userAccountControl",
                    updated.ToString(CultureInfo.InvariantCulture)));
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(string userDn, CancellationToken ct)
    {
        RequireDn(userDn);

        var memberOf = await WithSessionAsync(
            new DirectoryOperationInfo(DirectoryOperation.Read, userDn),
            session => session.RunAsync(
                ldap =>
                {
                    var request = new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, "memberOf");
                    var response = (SearchResponse)ldap.SendRequest(request);
                    return response.Entries.Count > 0
                        ? new SearchEntryAttributes(response.Entries[0]).GetStrings("memberOf")
                        : Array.Empty<string>();
                },
                ct),
            ct).ConfigureAwait(false);

        return memberOf
            .Select(dn => new GroupInfo(Dn.GetName(dn), dn))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GroupInfo>> SearchGroupsAsync(string text, int max, CancellationToken ct)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0 || max <= 0) return Array.Empty<GroupInfo>();

        return await WithSessionAsync(
            new DirectoryOperationInfo(DirectoryOperation.Read),
            async session =>
            {
                var rootDn = await session.RunAsync(LdapRootDse.ReadDefaultNamingContext, ct).ConfigureAwait(false);
                var request = new SearchRequest(rootDn, LdapFilters.GroupSearch(text), SearchScope.Subtree, DirectoryMappers.GroupAttributes);
                var entries = await session.RunAsync(ldap => PagedSearch.ReadUpTo(ldap, request, max), ct).ConfigureAwait(false);
                return (IReadOnlyList<GroupInfo>)entries
                    .Select(e => DirectoryMappers.MapGroup(e.DistinguishedName, new SearchEntryAttributes(e)))
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            },
            ct).ConfigureAwait(false);
    }

    public async Task AddToGroupAsync(string userDn, string groupDn, CancellationToken ct)
    {
        RequireDn(userDn);
        RequireDn(groupDn);
        await ModifyAsync(
            new DirectoryOperationInfo(DirectoryOperation.AddToGroup, userDn, groupDn),
            ldap => ldap.SendRequest(new ModifyRequest(groupDn, DirectoryAttributeOperation.Add, "member", userDn)),
            ct).ConfigureAwait(false);
    }

    public async Task RemoveFromGroupAsync(string userDn, string groupDn, CancellationToken ct)
    {
        RequireDn(userDn);
        RequireDn(groupDn);
        await ModifyAsync(
            new DirectoryOperationInfo(DirectoryOperation.RemoveFromGroup, userDn, groupDn),
            ldap => ldap.SendRequest(new ModifyRequest(groupDn, DirectoryAttributeOperation.Delete, "member", userDn)),
            ct).ConfigureAwait(false);
    }

    private static void RequireDn(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
            throw new OperationFailedException("Не указан объект каталога.", OperationFailure.InvalidInput);
    }

    private static List<UserInfo> MapUsers(IEnumerable<SearchResultEntry> entries) =>
        entries
            .Select(e => DirectoryMappers.MapUser(e.DistinguishedName, new SearchEntryAttributes(e)))
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();

    /// <summary>Выполняет изменение в каталоге на отдельном подключении.</summary>
    private Task ModifyAsync(DirectoryOperationInfo info, Action<LdapConnection> work, CancellationToken ct) =>
        WithSessionAsync(
            info,
            session => session.RunAsync(
                ldap =>
                {
                    work(ldap);
                    return true;
                },
                ct),
            ct);

    /// <summary>Открывает подключение на время <paramref name="operation"/> и переводит ошибки LDAP в понятные.</summary>
    private async Task<T> WithSessionAsync<T>(DirectoryOperationInfo info, Func<LdapSession, Task<T>> operation, CancellationToken ct)
    {
        var connection = _current.Require();
        try
        {
            using var session = await LdapSession.OpenAsync(connection, ct).ConfigureAwait(false);
            return await operation(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw LdapDirectoryErrors.Translate(ex, info);
        }
    }
}
