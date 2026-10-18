using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows.Hosts.Tests;

/// <summary>Подключение-заглушка: олицетворение выполняет действие на месте и считает вызовы.</summary>
internal sealed class FakeConnection : IDomainConnection
{
    private int _impersonationCount;

    public int ImpersonationCount => _impersonationCount;

    public string DomainName => "corp.local";
    public string? DomainController => null;
    public string AccountName => @"CORP\tester";
    public bool UsesExplicitCredentials => Credential is not null;
    public string AuthenticationKind => "Test";
    public AccountCredential? Credential { get; init; }

    public T RunImpersonated<T>(Func<T> action)
    {
        Interlocked.Increment(ref _impersonationCount);
        return action();
    }

    public void RunImpersonated(Action action)
    {
        Interlocked.Increment(ref _impersonationCount);
        action();
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    private int _loadCount;

    public AppSettings Settings { get; set; } = new();
    public int LoadCount => _loadCount;

    public AppSettings Load()
    {
        Interlocked.Increment(ref _loadCount);
        return Settings;
    }

    public void Save(AppSettings settings) => Settings = settings;
}

internal static class TestConnections
{
    public static CurrentConnection LoggedIn(FakeConnection connection)
    {
        var current = new CurrentConnection();
        current.Set(connection);
        return current;
    }

    public static CurrentConnection LoggedOut() => new();
}

/// <summary>Заглушка IHostProbe: открыты только перечисленные порты.</summary>
internal sealed class FakeProbe : IHostProbe
{
    private readonly HashSet<int> _open;

    public FakeProbe(params int[] openPorts)
    {
        _open = [.. openPorts];
    }

    public Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(_open.Contains(port));

    public Task<bool> IsOnlineAsync(string host, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(_open.Contains(445));
}

internal static class TestComputers
{
    public static ComputerInfo Pc(string name = "PC-ACC-014", string? dns = "pc-acc-014.corp.local") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DnsName = dns,
        DistinguishedName = $"CN={name},OU=Office,DC=corp,DC=local",
    };
}
