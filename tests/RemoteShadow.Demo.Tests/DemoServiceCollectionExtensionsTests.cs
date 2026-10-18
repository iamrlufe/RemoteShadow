using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo.Tests;

public class DemoServiceCollectionExtensionsTests
{
    private static (TestServiceCollection Services, TestServiceProvider Provider) Build()
    {
        var services = new TestServiceCollection();
        services.AddRemoteShadowDemo();
        return (services, new TestServiceProvider(services));
    }

    [Fact]
    public void Registers_EveryCoreInterface_AsSingletonOfDemoImplementation()
    {
        var (services, provider) = Build();

        var expected = new (Type Service, Type Implementation)[]
        {
            (typeof(IComputerDirectory), typeof(DemoComputerDirectory)),
            (typeof(IUserDirectory), typeof(DemoUserDirectory)),
            (typeof(ISessionService), typeof(DemoSessionService)),
            (typeof(IHostProbe), typeof(DemoHostProbe)),
            (typeof(IProcessLauncher), typeof(DemoProcessLauncher)),
            (typeof(IDiagnosticsService), typeof(DemoDiagnosticsService)),
            (typeof(ICredentialStore), typeof(DemoCredentialStore)),
            (typeof(IDomainLogonService), typeof(DemoDomainLogonService)),
        };

        foreach (var (service, implementation) in expected)
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

            var resolved = provider.GetService(service);
            Assert.NotNull(resolved);
            Assert.IsType(implementation, resolved);
            Assert.Same(resolved, provider.GetService(service));

            // Сам класс Demo* тоже доступен и это тот же экземпляр: оболочка подписывается на его события.
            Assert.Same(resolved, provider.GetService(implementation));
        }
    }

    [Fact]
    public void Registers_DemoDataAsSingleton_WithDefaultDomain()
    {
        var (services, provider) = Build();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(DemoData));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        var data = (DemoData)provider.GetService(typeof(DemoData))!;
        Assert.Same(data, provider.GetService(typeof(DemoData)));
        Assert.Equal(DemoData.DefaultComputerCount, data.ComputerCount);
        Assert.Equal(230, data.ComputerCount);
    }

    [Fact]
    public void Services_ShareTheSameData()
    {
        var (_, provider) = Build();
        var data = (DemoData)provider.GetService(typeof(DemoData))!;
        var launcher = (DemoProcessLauncher)provider.GetService(typeof(IProcessLauncher))!;
        var directory = (DemoComputerDirectory)provider.GetService(typeof(IComputerDirectory))!;

        var added = directory.Add("PC-DI-001", "OU=Склад,DC=corp,DC=local");

        Assert.Equal(added.Id, data.FindComputerByHost("PC-DI-001")!.Id);
        Assert.Equal("C:/Windows/System32/mstsc.exe", launcher.FindExecutable("mstsc.exe"));
    }

    [Fact]
    public void DoesNotRegister_CurrentConnection()
    {
        var (services, provider) = Build();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ICurrentConnection));
        Assert.Null(provider.GetService(typeof(ICurrentConnection)));
        Assert.Null(provider.GetService(typeof(IDomainConnection)));
    }

    [Fact]
    public void LogonService_ComesWithDefaultDemoEnvironment()
    {
        var (_, provider) = Build();
        var logon = (IDomainLogonService)provider.GetService(typeof(IDomainLogonService))!;

        var environment = logon.DetectEnvironment();

        Assert.True(environment.IsDomainJoined);
        Assert.Equal("corp.local", environment.DomainName);
        Assert.Equal("DEMO-PC", environment.ComputerName);
        Assert.Equal("demo-admin", environment.CurrentUser);
    }

    [Fact]
    public void Overload_WithPreparedData_UsesIt()
    {
        var data = TestSupport.Data(50, 3);
        var services = new TestServiceCollection();
        services.AddRemoteShadowDemo(data, DemoTimings.Instant);
        var provider = new TestServiceProvider(services);

        Assert.Same(data, provider.GetService(typeof(DemoData)));
        Assert.Same(DemoTimings.Instant, provider.GetService(typeof(DemoTimings)));
        Assert.IsType<DemoSessionService>(provider.GetService(typeof(ISessionService)));
    }

    [Fact]
    public void Registration_ReturnsSameCollection_AndRejectsNull()
    {
        var services = new TestServiceCollection();

        Assert.Same(services, services.AddRemoteShadowDemo());
        Assert.Throws<ArgumentNullException>(() => DemoServiceCollectionExtensions.AddRemoteShadowDemo(null!));
        Assert.Throws<ArgumentNullException>(() => new TestServiceCollection().AddRemoteShadowDemo(null!));
    }
}
