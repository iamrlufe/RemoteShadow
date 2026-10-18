using RemoteShadow.Core.Models;

namespace RemoteShadow.Core.Services;

/// <summary>Компьютеры и подразделения AD. Реализации: LDAP (Windows) и тестовые данные (Demo).</summary>
public interface IComputerDirectory
{
    /// <summary>DN корня домена: DC=corp,DC=local.</summary>
    Task<string> GetRootDnAsync(CancellationToken ct);

    /// <summary>
    /// Компьютеры домена постранично (500 на страницу), по мере прихода.
    /// Ключ записи — objectGUID. Для частичного обновления передают <see cref="ComputerQuery.ChangedSince"/>.
    /// </summary>
    IAsyncEnumerable<ComputerInfo> EnumerateAsync(ComputerQuery query, CancellationToken ct);

    /// <summary>Подразделения и контейнеры на один уровень ниже <paramref name="parentDn"/> (SearchScope.OneLevel).</summary>
    Task<IReadOnlyList<OuNode>> GetOuChildrenAsync(string parentDn, CancellationToken ct);

    /// <summary>Контроллер домена, с которого берутся данные. Если он сменился, следующее обновление индекса — полное.</summary>
    string? DomainController { get; }
}

/// <summary>Пользователи и группы AD.</summary>
public interface IUserDirectory
{
    /// <summary>Поиск по ФИО, логину, отделу, телефону. Ограничен <paramref name="max"/> записями.</summary>
    Task<IReadOnlyList<UserInfo>> SearchAsync(string text, int max, CancellationToken ct);

    Task<UserInfo?> GetBySamAccountNameAsync(string samAccountName, CancellationToken ct);

    /// <summary>Сброс пароля (unicodePwd) — только по шифрованному каналу. При нехватке прав — <see cref="OperationFailedException"/> с понятным текстом.</summary>
    Task ResetPasswordAsync(string userDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken ct);

    Task UnlockAsync(string userDn, CancellationToken ct);
    Task SetEnabledAsync(string userDn, bool enabled, CancellationToken ct);

    /// <summary>Группы, в которых состоит пользователь (прямое членство).</summary>
    Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(string userDn, CancellationToken ct);

    Task<IReadOnlyList<GroupInfo>> SearchGroupsAsync(string text, int max, CancellationToken ct);
    Task AddToGroupAsync(string userDn, string groupDn, CancellationToken ct);
    Task RemoveFromGroupAsync(string userDn, string groupDn, CancellationToken ct);
}
