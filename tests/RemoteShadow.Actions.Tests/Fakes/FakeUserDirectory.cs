using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Actions.Tests;

public sealed class FakeUserDirectory : IUserDirectory
{
    public sealed record PasswordReset(string UserDn, string Password, bool MustChange);

    public List<PasswordReset> PasswordResets { get; } = new();
    public List<string> Unlocks { get; } = new();
    public List<(string UserDn, bool Enabled)> EnabledChanges { get; } = new();
    public List<(string UserDn, string GroupDn)> Added { get; } = new();
    public List<(string UserDn, string GroupDn)> Removed { get; } = new();
    public List<(string Text, int Max)> GroupSearches { get; } = new();

    /// <summary>Группы, которые вернёт <see cref="GetGroupsAsync"/>.</summary>
    public List<GroupInfo> Groups { get; } = new();

    /// <summary>Группы, которые вернёт <see cref="SearchGroupsAsync"/>.</summary>
    public List<GroupInfo> FoundGroups { get; } = new();

    /// <summary>Если задано, <see cref="ResetPasswordAsync"/> бросает это исключение.</summary>
    public Exception? ResetPasswordFailure { get; set; }

    public Task<IReadOnlyList<UserInfo>> SearchAsync(string text, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserInfo>>(Array.Empty<UserInfo>());

    public Task<UserInfo?> GetBySamAccountNameAsync(string samAccountName, CancellationToken ct) =>
        Task.FromResult<UserInfo?>(null);

    public Task ResetPasswordAsync(string userDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken ct)
    {
        if (ResetPasswordFailure is not null) throw ResetPasswordFailure;
        PasswordResets.Add(new PasswordReset(userDn, newPassword, mustChangeAtNextLogon));
        return Task.CompletedTask;
    }

    public Task UnlockAsync(string userDn, CancellationToken ct)
    {
        Unlocks.Add(userDn);
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(string userDn, bool enabled, CancellationToken ct)
    {
        EnabledChanges.Add((userDn, enabled));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(string userDn, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GroupInfo>>(Groups.ToList());

    public Task<IReadOnlyList<GroupInfo>> SearchGroupsAsync(string text, int max, CancellationToken ct)
    {
        GroupSearches.Add((text, max));
        return Task.FromResult<IReadOnlyList<GroupInfo>>(FoundGroups.ToList());
    }

    public Task AddToGroupAsync(string userDn, string groupDn, CancellationToken ct)
    {
        Added.Add((userDn, groupDn));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string userDn, string groupDn, CancellationToken ct)
    {
        Removed.Add((userDn, groupDn));
        return Task.CompletedTask;
    }
}
