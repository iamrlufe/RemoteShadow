using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Demo.Tests;

public class DemoUserDirectoryTests
{
    private const string GoodPassword = "Str0ng!Pass-2026";

    private static (DemoData Data, DemoUserDirectory Dir) Create(int count = 230, int seed = 1)
    {
        var data = TestSupport.Data(count, seed);
        return (data, new DemoUserDirectory(data));
    }

    private static UserInfo Ordinary(DemoData data) =>
        data.GetUsers().First(u => u.Enabled && !u.LockedOut && !u.PasswordNeverExpires && !data.IsProtected(u.Id));

    private static UserInfo Protected(DemoData data) => data.GetUsers().First(u => data.IsProtected(u.Id));

    // ---------- поиск ----------

    [Fact]
    public async Task Search_ByLoginAnyCase_PutsExactMatchFirst()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        var found = await dir.SearchAsync(user.SamAccountName.ToUpperInvariant(), 10, CancellationToken.None);

        Assert.Equal(user.Id, found[0].Id);
    }

    [Fact]
    public async Task Search_BySurname_FindsUser()
    {
        var (data, dir) = Create();
        var user = data.GetUsers().First(u => u.DisplayName!.Contains(' '));
        var surname = user.DisplayName!.Split(' ')[0];

        var found = await dir.SearchAsync(surname.ToLowerInvariant(), 100, CancellationToken.None);

        Assert.Contains(found, u => u.Id == user.Id);
        Assert.All(found, u => Assert.Contains(surname, u.DisplayName!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_YoAndYe_AreTheSame()
    {
        var (data, dir) = Create(1500);
        var user = data.GetUsers().First(u => u.DisplayName!.Contains('ё'));
        var surname = user.DisplayName!.Split(' ')[0];

        var withYe = await dir.SearchAsync(surname.Replace('ё', 'е'), 1000, CancellationToken.None);

        Assert.Contains(withYe, u => u.Id == user.Id);
    }

    [Fact]
    public async Task Search_ByDepartmentAndPhone_Works()
    {
        var (data, dir) = Create();
        var withPhone = data.GetUsers().First(u => u.Phone is not null);

        var byDepartment = await dir.SearchAsync("бухгалтерия", 500, CancellationToken.None);
        var byPhone = await dir.SearchAsync(withPhone.Phone!, 50, CancellationToken.None);

        Assert.NotEmpty(byDepartment);
        Assert.Contains(byDepartment, u => u.Department == "Бухгалтерия");
        Assert.Contains(byPhone, u => u.Id == withPhone.Id);
    }

    [Fact]
    public async Task Search_RespectsMaxAndEmptyQueryListsEveryone()
    {
        var (data, dir) = Create();

        Assert.Equal(5, (await dir.SearchAsync(string.Empty, 5, CancellationToken.None)).Count);
        Assert.Equal(data.GetUsers().Count, (await dir.SearchAsync(" ", 10_000, CancellationToken.None)).Count);
        Assert.Empty(await dir.SearchAsync("a", 0, CancellationToken.None));
        Assert.Empty(await dir.SearchAsync("a", -3, CancellationToken.None));
    }

    [Theory]
    [InlineData("zzzzzzzz")]
    [InlineData("*")]
    [InlineData("(&(objectClass=*))")]
    [InlineData("\\00")]
    public async Task Search_SpecialCharactersAndNoMatches_ReturnEmpty(string text)
    {
        var (_, dir) = Create();

        Assert.Empty(await dir.SearchAsync(text, 10, CancellationToken.None));
    }

    [Fact]
    public async Task GetBySamAccountName_AcceptsLoginDomainAndUpnForms()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        var plain = await dir.GetBySamAccountNameAsync(user.SamAccountName.ToUpperInvariant(), CancellationToken.None);
        var withDomain = await dir.GetBySamAccountNameAsync($"CORP\\{user.SamAccountName}", CancellationToken.None);
        var upn = await dir.GetBySamAccountNameAsync(user.UserPrincipalName!, CancellationToken.None);

        Assert.Equal(user.Id, plain?.Id);
        Assert.Equal(user.Id, withDomain?.Id);
        Assert.Equal(user.Id, upn?.Id);
        Assert.Null(await dir.GetBySamAccountNameAsync("no-such-user", CancellationToken.None));
    }

    // ---------- пароль ----------

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("Ab1!xyz")]
    public async Task ResetPassword_TooShort_IsPolicyViolation(string password)
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => dir.ResetPasswordAsync(user.DistinguishedName, password, false, CancellationToken.None));

        Assert.Equal(OperationFailure.PolicyViolation, ex.Failure);
        Assert.Contains("8", ex.Message);
        Assert.Empty(dir.PasswordResets);
        Assert.Equal(user, data.FindUserById(user.Id));
    }

    [Fact]
    public async Task ResetPassword_EightCharacters_IsAccepted()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        await dir.ResetPasswordAsync(user.DistinguishedName, "12345678", false, CancellationToken.None);

        Assert.Single(dir.PasswordResets);
    }

    [Fact]
    public async Task ResetPassword_RemembersOnlyTheFact_NotThePassword()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);
        var before = DateTime.UtcNow;

        await dir.ResetPasswordAsync(user.DistinguishedName, GoodPassword, mustChangeAtNextLogon: false, CancellationToken.None);

        var reset = Assert.Single(dir.PasswordResets);
        Assert.Equal(user.SamAccountName, reset.SamAccountName);
        Assert.False(reset.MustChangeAtNextLogon);
        Assert.True(reset.TimeUtc >= before);

        // Пароля нет ни в записи, ни в объекте пользователя.
        Assert.DoesNotContain(GoodPassword, reset.ToString());
        Assert.DoesNotContain(GoodPassword, data.FindUserById(user.Id)!.ToString());

        var updated = data.FindUserById(user.Id)!;
        Assert.True(updated.PasswordLastSet >= before);
        Assert.Equal(updated.PasswordLastSet!.Value.AddDays(90), updated.PasswordExpires);
    }

    [Fact]
    public async Task ResetPassword_MustChange_ClearsPasswordLastSet()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        await dir.ResetPasswordAsync(user.DistinguishedName, GoodPassword, mustChangeAtNextLogon: true, CancellationToken.None);

        var updated = data.FindUserById(user.Id)!;
        Assert.Null(updated.PasswordLastSet);
        Assert.Null(updated.PasswordExpires);
        Assert.True(Assert.Single(dir.PasswordResets).MustChangeAtNextLogon);
    }

    [Fact]
    public async Task ResetPassword_NeverExpiringPassword_StaysNeverExpiring()
    {
        var (data, dir) = Create(1500);
        var user = data.GetUsers().First(u => u.PasswordNeverExpires && u.Enabled && !data.IsProtected(u.Id));

        await dir.ResetPasswordAsync(user.DistinguishedName, GoodPassword, false, CancellationToken.None);

        Assert.Null(data.FindUserById(user.Id)!.PasswordExpires);
    }

    [Fact]
    public async Task ResetPassword_FindsUserByDnInAnyCase()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        await dir.ResetPasswordAsync(user.DistinguishedName.ToUpperInvariant(), GoodPassword, false, CancellationToken.None);

        Assert.Single(dir.PasswordResets);
    }

    // ---------- права: группа Protected ----------

    [Fact]
    public async Task ProtectedUser_ResetPassword_IsAccessDeniedWithClearMessage()
    {
        var (data, dir) = Create();
        var user = Protected(data);

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => dir.ResetPasswordAsync(user.DistinguishedName, GoodPassword, false, CancellationToken.None));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains(user.SamAccountName, ex.Message);
        Assert.Contains("Protected", ex.Message);
        Assert.DoesNotContain(GoodPassword, ex.Message);
        Assert.Empty(dir.PasswordResets);
        Assert.Equal(user, data.FindUserById(user.Id));
    }

    [Fact]
    public async Task ProtectedUser_UnlockAndEnableAndDisable_AreAccessDenied()
    {
        var (data, dir) = Create();
        var user = Protected(data);

        foreach (var operation in new Func<Task>[]
                 {
                     () => dir.UnlockAsync(user.DistinguishedName, CancellationToken.None),
                     () => dir.SetEnabledAsync(user.DistinguishedName, false, CancellationToken.None),
                     () => dir.SetEnabledAsync(user.DistinguishedName, true, CancellationToken.None),
                 })
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(operation);
            Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
            Assert.Contains("Protected", ex.Message);
        }

        Assert.Equal(user, data.FindUserById(user.Id));
    }

    [Fact]
    public async Task ProtectedUser_AccessCheckComesBeforePasswordPolicy()
    {
        var (data, dir) = Create();

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => dir.ResetPasswordAsync(Protected(data).DistinguishedName, "1", false, CancellationToken.None));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
    }

    [Fact]
    public async Task ProtectedUser_CanStillBeReadAndGroupsListed()
    {
        var (data, dir) = Create();
        var user = Protected(data);

        Assert.NotNull(await dir.GetBySamAccountNameAsync(user.SamAccountName, CancellationToken.None));
        Assert.Contains(await dir.GetGroupsAsync(user.DistinguishedName, CancellationToken.None), g => g.Name == "Protected");
    }

    // ---------- блокировка и включение ----------

    [Fact]
    public async Task Unlock_ClearsLockout()
    {
        var (data, dir) = Create(1500);
        var user = data.GetUsers().First(u => u.LockedOut && !data.IsProtected(u.Id));

        await dir.UnlockAsync(user.DistinguishedName, CancellationToken.None);

        Assert.False(data.FindUserById(user.Id)!.LockedOut);
    }

    [Fact]
    public async Task SetEnabled_TogglesAccount()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        await dir.SetEnabledAsync(user.DistinguishedName, false, CancellationToken.None);
        Assert.False(data.FindUserById(user.Id)!.Enabled);

        await dir.SetEnabledAsync(user.DistinguishedName, true, CancellationToken.None);
        Assert.True(data.FindUserById(user.Id)!.Enabled);
    }

    [Fact]
    public async Task UnknownUser_IsNotFound()
    {
        var (_, dir) = Create();
        const string dn = "CN=Нет Такого,OU=ИТ,OU=Офис,DC=corp,DC=local";

        foreach (var operation in new Func<Task>[]
                 {
                     () => dir.ResetPasswordAsync(dn, GoodPassword, false, CancellationToken.None),
                     () => dir.UnlockAsync(dn, CancellationToken.None),
                     () => dir.SetEnabledAsync(dn, true, CancellationToken.None),
                     () => dir.GetGroupsAsync(dn, CancellationToken.None),
                     () => dir.AddToGroupAsync(dn, "CN=VPN-Users,CN=Users,DC=corp,DC=local", CancellationToken.None),
                     () => dir.RemoveFromGroupAsync(dn, "CN=VPN-Users,CN=Users,DC=corp,DC=local", CancellationToken.None),
                 })
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(operation);
            Assert.Equal(OperationFailure.NotFound, ex.Failure);
        }
    }

    // ---------- группы ----------

    [Fact]
    public async Task GetGroups_ReturnsDirectMembership()
    {
        var (data, dir) = Create();
        var user = data.GetUsers().First(u => u.Department == "Бухгалтерия");

        var groups = await dir.GetGroupsAsync(user.DistinguishedName, CancellationToken.None);

        Assert.Contains(groups, g => g.Name == "Domain Users");
        Assert.Contains(groups, g => g.Name == "Бухгалтерия");
        Assert.Equal(data.GetGroupsOf(user.Id).Select(g => g.Name).Order(), groups.Select(g => g.Name).Order());
    }

    [Fact]
    public async Task SearchGroups_FindsByNameAndDescription_AndRespectsMax()
    {
        var (data, dir) = Create();

        var byName = await dir.SearchGroupsAsync("vpn", 10, CancellationToken.None);
        var byDescription = await dir.SearchGroupsAsync("удалённый доступ", 10, CancellationToken.None);
        var all = await dir.SearchGroupsAsync(string.Empty, 1000, CancellationToken.None);
        var limited = await dir.SearchGroupsAsync(string.Empty, 3, CancellationToken.None);

        Assert.Equal("VPN-Users", Assert.Single(byName).Name);
        Assert.Contains(byDescription, g => g.Name == "VPN-Users");
        Assert.Equal(data.GetGroups().Count, all.Count);
        Assert.Equal(3, limited.Count);
        Assert.Empty(await dir.SearchGroupsAsync("нет-такой-группы", 10, CancellationToken.None));
    }

    [Fact]
    public async Task AddToGroup_AddsOnce_AndRemoveFromGroupRemovesOnce()
    {
        var (data, dir) = Create();
        var group = data.GetGroups().First(g => g.Name == "Helpdesk");
        var user = data.GetUsers().First(u => u.Enabled && !data.IsProtected(u.Id) && !data.IsMember(u.Id, group.Name));

        await dir.AddToGroupAsync(user.DistinguishedName, group.DistinguishedName, CancellationToken.None);

        Assert.True(data.IsMember(user.Id, group.Name));
        Assert.Contains(await dir.GetGroupsAsync(user.DistinguishedName, CancellationToken.None), g => g.Name == group.Name);
        Assert.Contains(data.GetGroupMembers(group.DistinguishedName), u => u.Id == user.Id);

        var again = await Assert.ThrowsAsync<OperationFailedException>(
            () => dir.AddToGroupAsync(user.DistinguishedName, group.DistinguishedName, CancellationToken.None));
        Assert.Equal(OperationFailure.InvalidInput, again.Failure);
        Assert.Contains(group.Name, again.Message);

        await dir.RemoveFromGroupAsync(user.DistinguishedName, group.DistinguishedName, CancellationToken.None);

        Assert.False(data.IsMember(user.Id, group.Name));
        var twice = await Assert.ThrowsAsync<OperationFailedException>(
            () => dir.RemoveFromGroupAsync(user.DistinguishedName, group.DistinguishedName, CancellationToken.None));
        Assert.Equal(OperationFailure.InvalidInput, twice.Failure);
    }

    [Fact]
    public async Task UnknownGroup_IsNotFound()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);

        var add = await Assert.ThrowsAsync<OperationFailedException>(
            () => dir.AddToGroupAsync(user.DistinguishedName, "CN=Нет такой,CN=Users,DC=corp,DC=local", CancellationToken.None));
        var remove = await Assert.ThrowsAsync<OperationFailedException>(
            () => dir.RemoveFromGroupAsync(user.DistinguishedName, "CN=Нет такой,CN=Users,DC=corp,DC=local", CancellationToken.None));

        Assert.Equal(OperationFailure.NotFound, add.Failure);
        Assert.Equal(OperationFailure.NotFound, remove.Failure);
    }

    [Fact]
    public async Task EveryOperation_HonoursCancellation()
    {
        var (data, dir) = Create();
        var user = Ordinary(data);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.SearchAsync("a", 5, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.GetBySamAccountNameAsync("a", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.ResetPasswordAsync(user.DistinguishedName, GoodPassword, false, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.UnlockAsync(user.DistinguishedName, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.SetEnabledAsync(user.DistinguishedName, false, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.GetGroupsAsync(user.DistinguishedName, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.SearchGroupsAsync("a", 5, cts.Token));
        Assert.Empty(dir.PasswordResets);
        Assert.True(data.FindUserById(user.Id)!.Enabled);
    }

    [Fact]
    public async Task ConcurrentChanges_DoNotCorruptState()
    {
        var (data, dir) = Create(500);
        var users = data.GetUsers().Where(u => !data.IsProtected(u.Id)).Take(50).ToArray();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        await Task.Run(() => Parallel.For(0, 400, i =>
        {
            try
            {
                var user = users[i % users.Length];
                dir.SetEnabledAsync(user.DistinguishedName, i % 2 == 0, CancellationToken.None).GetAwaiter().GetResult();
                dir.ResetPasswordAsync(user.DistinguishedName, GoodPassword, i % 3 == 0, CancellationToken.None).GetAwaiter().GetResult();
                _ = dir.SearchAsync(user.SamAccountName, 5, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                errors.Add(e);
            }
        }));

        Assert.Empty(errors);
        Assert.Equal(400, dir.PasswordResets.Count);
    }
}
