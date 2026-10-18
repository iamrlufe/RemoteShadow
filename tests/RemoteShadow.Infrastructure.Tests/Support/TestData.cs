using RemoteShadow.Core.Models;

namespace RemoteShadow.Infrastructure.Tests;

public static class TestData
{
    public const string Root = "DC=corp,DC=local";
    public const string OfficeOu = "OU=Офис," + Root;
    public const string AccountingOu = "OU=Бухгалтерия,OU=Офис," + Root;
    public const string WarehouseOu = "OU=Склад,OU=Офис," + Root;

    public static Guid IdOf(int n) => new(n, 0, 0, new byte[8]);

    public static ComputerInfo Pc(
        string name,
        string ou = AccountingOu,
        int id = 0,
        string? description = null,
        bool enabled = true,
        DateTime? whenChanged = null,
        string? dnsName = null,
        bool? online = null)
    {
        return new ComputerInfo
        {
            Id = id != 0 ? IdOf(id) : Guid.NewGuid(),
            Name = name,
            DnsName = dnsName ?? $"{name.ToLowerInvariant()}.corp.local",
            Description = description,
            OperatingSystem = "Windows 11 Pro",
            DistinguishedName = $"CN={name},{ou}",
            Enabled = enabled,
            WhenChanged = whenChanged ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsOnline = online,
        };
    }

    public static SessionInfo Session(int id, string? user, SessionState state = SessionState.Active, string? domain = "CORP") =>
        new() { Id = id, UserName = user, Domain = domain, State = state, WinStationName = "rdp-tcp#" + id };

    public static UserInfo User(string sam, string? displayName = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            SamAccountName = sam,
            DisplayName = displayName,
            DistinguishedName = $"CN={displayName ?? sam},OU=Люди,{Root}",
        };
}
