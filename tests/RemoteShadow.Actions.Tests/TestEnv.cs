using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Actions.Tests;

/// <summary>Минимальный контейнер: возвращает только то, что в него положили.</summary>
public sealed class SimpleServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    public SimpleServiceProvider Add<T>(T service) where T : notnull
    {
        _services[typeof(T)] = service;
        return this;
    }

    public object? GetService(Type serviceType) =>
        _services.TryGetValue(serviceType, out var s) ? s : null;
}

/// <summary>Заглушки сервисов Core и фабрика <see cref="ActionContext"/> для тестов действий.</summary>
public sealed class TestEnv
{
    public TestEnv(string accountName = @"CORP\adm-ivanov")
    {
        CurrentConnection = new CurrentConnection();
        CurrentConnection.Set(new FakeDomainConnection(accountName));

        Services = new SimpleServiceProvider()
            .Add<ICurrentConnection>(CurrentConnection)
            .Add<IDialogService>(Dialogs)
            .Add<IProcessLauncher>(Launcher)
            .Add<ISessionService>(Sessions)
            .Add<IUserDirectory>(Users)
            .Add<IDiagnosticsService>(Diagnostics);
    }

    public FakeProcessLauncher Launcher { get; } = new();
    public FakeSessionService Sessions { get; } = new();
    public FakeUserDirectory Users { get; } = new();
    public FakeDialogService Dialogs { get; } = new();
    public FakeDiagnosticsService Diagnostics { get; } = new();
    public CurrentConnection CurrentConnection { get; }
    public SimpleServiceProvider Services { get; }

    public ActionContext Context(IReadOnlyDictionary<string, string>? parameters = null, string? reason = null) =>
        new(Services, parameters, reason);

    public ActionContext With(params (string Key, string Value)[] parameters) =>
        new(Services, parameters.ToDictionary(p => p.Key, p => p.Value));
}

public static class Samples
{
    public const string HostDns = "pc-acc-014.corp.local";

    public static ComputerInfo Computer(string name = "PC-ACC-014", string? dnsName = HostDns) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DnsName = dnsName,
        DistinguishedName = $"CN={name},OU=Бухгалтерия,DC=corp,DC=local",
    };

    public static SessionInfo Session(
        int id = 1, string? user = "petrova", SessionState state = SessionState.Active, string? domain = "CORP") => new()
    {
        Id = id,
        UserName = user,
        Domain = domain,
        State = state,
    };

    public static UserInfo User(
        string sam = "petrova", string? displayName = "Петрова А.", bool enabled = true, bool lockedOut = false) => new()
    {
        Id = Guid.NewGuid(),
        SamAccountName = sam,
        DistinguishedName = $"CN={displayName ?? sam},OU=Люди,DC=corp,DC=local",
        DisplayName = displayName,
        Enabled = enabled,
        LockedOut = lockedOut,
    };

    public static SessionTarget SessionTarget(SessionInfo? session = null, ComputerInfo? computer = null) =>
        new(computer ?? Computer(), session ?? Session());

    public static ComputerTarget ComputerTarget(ComputerInfo? computer = null) => new(computer ?? Computer());

    public static UserTarget UserTarget(UserInfo? user = null) => new(user ?? User());
}
