using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo;

/// <summary>Факт сброса пароля. Сам пароль не хранится нигде.</summary>
public sealed record DemoPasswordReset(DateTime TimeUtc, string SamAccountName, bool MustChangeAtNextLogon);

/// <summary>
/// Тестовый каталог пользователей и групп. Все изменения живут в памяти (<see cref="DemoData"/>).
/// Ошибки как в настоящем AD: короткий пароль — нарушение политики, учётная запись из группы «Protected» — нет прав.
/// </summary>
public sealed class DemoUserDirectory : IUserDirectory
{
    /// <summary>Минимальная длина пароля в тестовой политике домена.</summary>
    public const int MinPasswordLength = 8;

    private static readonly TimeSpan MaxPasswordAge = TimeSpan.FromDays(90);

    private readonly DemoData _data;
    private readonly object _gate = new();
    private readonly List<DemoPasswordReset> _resets = new();

    public DemoUserDirectory(DemoData data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    /// <summary>Все сбросы пароля с начала работы: кто, когда и нужно ли сменить при входе.</summary>
    public IReadOnlyList<DemoPasswordReset> PasswordResets
    {
        get { lock (_gate) return _resets.ToArray(); }
    }

    public Task<IReadOnlyList<UserInfo>> SearchAsync(string text, int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);

        var users = _data.GetUsers();
        var query = Fold(text.Trim());

        IEnumerable<UserInfo> found;
        if (query.Length == 0)
        {
            found = users.OrderBy(u => u.ShownName, StringComparer.InvariantCultureIgnoreCase);
        }
        else
        {
            var digits = new string(query.Where(char.IsDigit).ToArray());
            found = users
                .Select(u => (User: u, Rank: Rank(u, query, digits)))
                .Where(x => x.Rank >= 0)
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.User.ShownName, StringComparer.InvariantCultureIgnoreCase)
                .Select(x => x.User);
        }

        return Task.FromResult<IReadOnlyList<UserInfo>>(found.Take(Math.Max(0, max)).ToList());
    }

    public Task<UserInfo?> GetBySamAccountNameAsync(string samAccountName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(samAccountName);

        // Принимаем и «ppetrova», и «CORP\ppetrova», и «ppetrova@corp.local».
        var login = AccountNameParser.Parse(samAccountName).UserName;
        return Task.FromResult(_data.FindUserBySamAccountName(login));
    }

    public Task ResetPasswordAsync(string userDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(newPassword);

        var user = RequireUser(userDn);
        RequireNotProtected(user, "сбросить пароль");

        if (newPassword.Length < MinPasswordLength)
        {
            throw new OperationFailedException(
                $"Пароль не соответствует политике домена: минимальная длина — {MinPasswordLength} символов.",
                OperationFailure.PolicyViolation);
        }

        var now = DateTime.UtcNow;
        _data.UpdateUser(user.Id, u => u with
        {
            // «Сменить при следующем входе» в AD — это pwdLastSet = 0.
            PasswordLastSet = mustChangeAtNextLogon ? null : now,
            PasswordExpires = mustChangeAtNextLogon || u.PasswordNeverExpires ? null : now + MaxPasswordAge,
        });

        lock (_gate) _resets.Add(new DemoPasswordReset(now, user.SamAccountName, mustChangeAtNextLogon));
        return Task.CompletedTask;
    }

    public Task UnlockAsync(string userDn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = RequireUser(userDn);
        RequireNotProtected(user, "разблокировать учётную запись");
        _data.UpdateUser(user.Id, u => u with { LockedOut = false });
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(string userDn, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = RequireUser(userDn);
        RequireNotProtected(user, enabled ? "включить учётную запись" : "отключить учётную запись");
        _data.UpdateUser(user.Id, u => u with { Enabled = enabled });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(string userDn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = RequireUser(userDn);
        return Task.FromResult(_data.GetGroupsOf(user.Id));
    }

    public Task<IReadOnlyList<GroupInfo>> SearchGroupsAsync(string text, int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);

        var query = Fold(text.Trim());
        IReadOnlyList<GroupInfo> found = _data.GetGroups()
            .Where(g => query.Length == 0 || Fold(g.Name).Contains(query) || Fold(g.Description ?? string.Empty).Contains(query))
            .OrderBy(g => Fold(g.Name).StartsWith(query, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.InvariantCultureIgnoreCase)
            .Take(Math.Max(0, max))
            .ToList();
        return Task.FromResult(found);
    }

    public Task AddToGroupAsync(string userDn, string groupDn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = RequireUser(userDn);
        var group = RequireGroup(groupDn);
        if (!_data.AddMember(group.DistinguishedName, user.Id))
        {
            throw new OperationFailedException(
                $"Пользователь «{user.ShownName}» уже входит в группу «{group.Name}».", OperationFailure.InvalidInput);
        }

        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string userDn, string groupDn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = RequireUser(userDn);
        var group = RequireGroup(groupDn);
        if (!_data.RemoveMember(group.DistinguishedName, user.Id))
        {
            throw new OperationFailedException(
                $"Пользователь «{user.ShownName}» не входит в группу «{group.Name}».", OperationFailure.InvalidInput);
        }

        return Task.CompletedTask;
    }

    private UserInfo RequireUser(string userDn)
    {
        ArgumentNullException.ThrowIfNull(userDn);
        return _data.FindUserByDn(userDn)
               ?? throw new OperationFailedException("Пользователь не найден в каталоге (он мог быть удалён).", OperationFailure.NotFound);
    }

    private GroupInfo RequireGroup(string groupDn)
    {
        ArgumentNullException.ThrowIfNull(groupDn);
        return _data.FindGroupByDn(groupDn)
               ?? throw new OperationFailedException("Группа не найдена в каталоге.", OperationFailure.NotFound);
    }

    private void RequireNotProtected(UserInfo user, string whatTried)
    {
        if (!_data.IsProtected(user.Id)) return;

        throw new OperationFailedException(
            $"Нельзя {whatTried}: учётная запись «{user.SamAccountName}» входит в защищённую группу «{DemoData.ProtectedGroupName}». "
            + "Такие записи меняют только администраторы домена (5).",
            OperationFailure.AccessDenied);
    }

    /// <summary>0 — точный логин, 1 — начало логина, 2 — начало ФИО, 3 — вхождение в ФИО, логин, отдел, должность или телефон; -1 — не подходит.</summary>
    private static int Rank(UserInfo u, string query, string queryDigits)
    {
        var sam = Fold(u.SamAccountName);
        if (sam == query) return 0;
        if (sam.StartsWith(query, StringComparison.Ordinal)) return 1;

        var display = Fold(u.DisplayName ?? string.Empty);
        if (display.StartsWith(query, StringComparison.Ordinal)) return 2;

        if (display.Contains(query) || sam.Contains(query)
            || Fold(u.Department ?? string.Empty).Contains(query)
            || Fold(u.Title ?? string.Empty).Contains(query)
            || Fold(u.Phone ?? string.Empty).Contains(query))
            return 3;

        if (queryDigits.Length >= 3 && new string((u.Phone ?? string.Empty).Where(char.IsDigit).ToArray()).Contains(queryDigits))
            return 3;

        return -1;
    }

    /// <summary>Нижний регистр и «ё» как «е»: в поиске по-русски их не различают.</summary>
    private static string Fold(string text) => text.ToLowerInvariant().Replace('ё', 'е');
}
