using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

/// <summary>
/// Минимальный контейнер поверх <see cref="IServiceCollection"/>: полного пакета DI в тестах нет, а проверить надо,
/// что зарегистрированные типы действительно собираются (хватает ли зависимостей, нет ли неоднозначных конструкторов).
/// </summary>
internal sealed class MiniContainer : IServiceProvider, IDisposable
{
    private readonly IServiceCollection _services;
    private readonly Dictionary<ServiceDescriptor, object> _singletons = new(ReferenceEqualityComparer.Instance);
    private readonly List<IDisposable> _disposables = new();

    public MiniContainer(IServiceCollection services)
    {
        _services = services;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider)) return this;

        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            var itemType = serviceType.GetGenericArguments()[0];
            var items = _services.Where(d => d.ServiceType == itemType).Select(Resolve).ToArray();
            var array = Array.CreateInstance(itemType, items.Length);
            items.CopyTo(array, 0);
            return array;
        }

        var descriptor = _services.LastOrDefault(d => d.ServiceType == serviceType);
        return descriptor is null ? null : Resolve(descriptor);
    }

    public T Get<T>() where T : notnull => (T)(GetService(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} не зарегистрирован."));

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
    }

    private object Resolve(ServiceDescriptor descriptor)
    {
        lock (_singletons)
        {
            if (_singletons.TryGetValue(descriptor, out var existing)) return existing;

            var created = descriptor.ImplementationInstance
                          ?? descriptor.ImplementationFactory?.Invoke(this)
                          ?? ActivatorUtilities.CreateInstance(this, descriptor.ImplementationType!);
            _singletons[descriptor] = created;
            if (created is IDisposable disposable && descriptor.ImplementationInstance is null) _disposables.Add(disposable);
            return created;
        }
    }
}

public class ServiceRegistrationTests
{
    private static ServiceDescriptor Single(IServiceCollection services, Type serviceType) =>
        Assert.Single(services, d => d.ServiceType == serviceType);

    private static ServiceCollection WithExternalServices(TempDir temp, out FakeComputerDirectory directory)
    {
        directory = new FakeComputerDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<IComputerDirectory>(directory);
        services.AddSingleton<IUserDirectory>(new FakeUserDirectory());
        services.AddSingleton<ISessionService>(new FakeSessionService());
        services.AddSingleton<IHostProbe>(new FakeHostProbe());
        services.AddSingleton<IDialogService>(new FakeDialogService());
        services.AddSingleton<ICurrentConnection>(FakeCurrentConnection.LoggedIn());
        services.AddSingleton(new AppPaths(temp.Path));
        services.AddRemoteShadowInfrastructure();
        return services;
    }

    [Theory]
    [InlineData(typeof(ISettingsStore), typeof(JsonSettingsStore))]
    [InlineData(typeof(IAuditLog), typeof(JsonAuditLog))]
    [InlineData(typeof(ISessionCache), typeof(SessionCache))]
    [InlineData(typeof(IComputerIndex), typeof(ComputerIndex))]
    [InlineData(typeof(IOnlineStatusService), typeof(OnlineStatusService))]
    [InlineData(typeof(IUserLocator), typeof(UserLocator))]
    [InlineData(typeof(IActionRunner), typeof(ActionRunner))]
    public void Core_services_are_registered_as_singletons(Type service, Type implementation)
    {
        var services = new ServiceCollection().AddRemoteShadowInfrastructure();

        var descriptor = Single(services, service);

        Assert.Equal(implementation, descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void Time_provider_and_app_paths_are_registered_with_defaults()
    {
        var services = new ServiceCollection().AddRemoteShadowInfrastructure();

        Assert.Same(TimeProvider.System, Single(services, typeof(TimeProvider)).ImplementationInstance);
        var paths = Assert.IsType<AppPaths>(Single(services, typeof(AppPaths)).ImplementationInstance);
        Assert.EndsWith(Path.Combine("RemoteShadow", "settings.json"), paths.SettingsFile);
    }

    [Fact]
    public void Current_connection_and_windows_dependent_services_are_left_to_others()
    {
        var services = new ServiceCollection().AddRemoteShadowInfrastructure();

        foreach (var type in new[]
                 {
                     typeof(ICurrentConnection), typeof(IDomainConnection), typeof(IDomainLogonService), typeof(ICredentialStore),
                     typeof(IComputerDirectory), typeof(IUserDirectory), typeof(ISessionService), typeof(IHostProbe),
                     typeof(IProcessLauncher), typeof(IDialogService), typeof(INotificationService), typeof(IDiagnosticsService),
                 })
        {
            Assert.DoesNotContain(services, d => d.ServiceType == type);
        }
    }

    [Fact]
    public void Already_registered_services_are_kept_and_repeated_call_adds_nothing()
    {
        var ownTime = new ManualTimeProvider();
        var ownPaths = new AppPaths(Path.Combine(Path.GetTempPath(), "rs-own"));
        var ownSettings = new FakeSettingsStore();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(ownTime);
        services.AddSingleton(ownPaths);
        services.AddSingleton<ISettingsStore>(ownSettings);

        services.AddRemoteShadowInfrastructure();
        var countAfterFirst = services.Count;
        services.AddRemoteShadowInfrastructure();

        Assert.Equal(countAfterFirst, services.Count);
        Assert.Same(ownTime, Single(services, typeof(TimeProvider)).ImplementationInstance);
        Assert.Same(ownPaths, Single(services, typeof(AppPaths)).ImplementationInstance);
        Assert.Same(ownSettings, Single(services, typeof(ISettingsStore)).ImplementationInstance);
    }

    [Fact]
    public void Null_service_collection_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => InfrastructureServiceCollectionExtensions.AddRemoteShadowInfrastructure(null!));
    }

    [Fact]
    public void Every_service_can_be_built_and_is_a_singleton()
    {
        using var temp = new TempDir();
        using var container = new MiniContainer(WithExternalServices(temp, out _));

        foreach (var type in new[]
                 {
                     typeof(ISettingsStore), typeof(IAuditLog), typeof(ISessionCache), typeof(IComputerIndex),
                     typeof(IOnlineStatusService), typeof(IUserLocator), typeof(IActionRunner),
                 })
        {
            var first = container.GetService(type);
            Assert.NotNull(first);
            Assert.Same(first, container.GetService(type));
        }
    }

    [Fact]
    public void Services_share_the_same_index_cache_and_settings_instances()
    {
        using var temp = new TempDir();
        using var container = new MiniContainer(WithExternalServices(temp, out _));

        Assert.IsType<ComputerIndex>(container.Get<IComputerIndex>());
        Assert.IsType<JsonSettingsStore>(container.Get<ISettingsStore>());
        Assert.Same(container.Get<AppPaths>(), container.Get<AppPaths>());
        Assert.Equal(temp.Path, container.Get<AppPaths>().DataDir);
    }

    [Fact]
    public async Task Online_status_service_from_container_updates_the_index_from_container()
    {
        using var temp = new TempDir();
        var services = WithExternalServices(temp, out var directory);
        directory.Computers.Add(Pc("PC-1", id: 1));
        using var container = new MiniContainer(services);
        var index = container.Get<IComputerIndex>();
        await index.RefreshAsync(full: true, CancellationToken.None);

        await container.Get<IOnlineStatusService>().RefreshAsync(index.All.ToList(), force: true, CancellationToken.None);

        Assert.True(index.Find(IdOf(1))!.IsOnline);
    }

    [Fact]
    public async Task Action_runner_from_container_finds_registered_actions_and_writes_audit_into_app_paths()
    {
        using var temp = new TempDir();
        var services = WithExternalServices(temp, out _);
        services.AddAction<ComputerTarget, TestAction<ComputerTarget>>();
        using var container = new MiniContainer(services);
        var runner = container.Get<IActionRunner>();

        var action = Assert.Single(runner.GetActions<ComputerTarget>());
        var result = await runner.RunAsync(action, new[] { new ComputerTarget(Pc("PC-1", id: 1)) }, null, null, CancellationToken.None);

        Assert.True(result.AllOk);
        var log = container.Get<IAuditLog>();
        var records = await log.ReadRecentAsync(10);
        Assert.Equal("PC-1", Assert.Single(records).Target);
        Assert.Equal(@"CORP\adm-ivanov", records[0].Account);
        Assert.Single(Directory.GetFiles(temp.Paths.AuditDir, "*.jsonl"));
    }

    [Fact]
    public async Task User_locator_from_container_works_with_the_shared_cache()
    {
        using var temp = new TempDir();
        var services = WithExternalServices(temp, out var directory);
        var pc = Pc("PC-1", id: 1);
        directory.Computers.Add(pc);
        using var container = new MiniContainer(services);
        await container.Get<IComputerIndex>().RefreshAsync(full: true, CancellationToken.None);
        container.Get<ISessionCache>().Set(new SessionSnapshot(
            pc.Id, SessionListResult.Ok(new[] { Session(1, "ivanov") }), new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc)));

        var found = container.Get<IUserLocator>().FindCached("ivanov");

        Assert.Equal("PC-1", Assert.Single(found).Computer.Name);
    }
}
