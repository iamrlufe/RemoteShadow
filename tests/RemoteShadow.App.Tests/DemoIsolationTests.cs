using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Services;
using RemoteShadow.Infrastructure;

namespace RemoteShadow.App.Tests;

/// <summary>
/// Изоляция демо-режима: в контейнере --demo ни один сервис Core не должен разрешаться в реальную сетевую или
/// административную реализацию. Проверяется вся цепочка, а не только папка Demo.
/// </summary>
public sealed class DemoIsolationTests
{
    private static readonly string[] RealAssemblies = ["RemoteShadow.Windows", "RemoteShadow.Windows.Hosts"];

    [Fact]
    public void Every_core_service_resolves_to_a_demo_or_pure_infrastructure_type()
    {
        var fixture = new DemoFixture();
        var coreServices = typeof(IHostProbe).Assembly.GetExportedTypes()
            .Where(t => t.IsInterface && t.Namespace == typeof(IHostProbe).Namespace)
            .Where(t => fixture.Provider.GetService(t) is not null)
            .ToList();

        Assert.True(coreServices.Count >= 12, "ожидались сервисы Core, найдено " + coreServices.Count);
        foreach (var service in coreServices)
        {
            var implementation = fixture.Provider.GetRequiredService(service).GetType();
            var assembly = implementation.Assembly.GetName().Name!;
            Assert.DoesNotContain(assembly, RealAssemblies);
            Assert.False(
                implementation == typeof(DnsResolverService) || implementation == typeof(ReverseDnsResolver) || implementation == typeof(SubnetScanner),
                $"{service.Name} разрешается в реальную сетевую реализацию {implementation.Name}");
        }
    }

    [Theory]
    [InlineData(typeof(IHostProbe), "RemoteShadow.Demo")]
    [InlineData(typeof(IIcmpProbe), "RemoteShadow.Demo")]
    [InlineData(typeof(IDnsResolverService), "RemoteShadow.Demo")]
    [InlineData(typeof(IReverseDnsResolver), "RemoteShadow.Demo")]
    [InlineData(typeof(ISubnetScanner), "RemoteShadow.Demo")]
    [InlineData(typeof(ISessionService), "RemoteShadow.Demo")]
    [InlineData(typeof(IProcessLauncher), "RemoteShadow.Demo")]
    [InlineData(typeof(IDiagnosticsService), "RemoteShadow.Demo")]
    [InlineData(typeof(IComputerDirectory), "RemoteShadow.Demo")]
    [InlineData(typeof(IUserDirectory), "RemoteShadow.Demo")]
    [InlineData(typeof(IDomainLogonService), "RemoteShadow.Demo")]
    [InlineData(typeof(ICredentialStore), "RemoteShadow.Demo")]
    public void Network_and_admin_services_come_from_demo_assembly(Type service, string expectedAssembly)
    {
        var fixture = new DemoFixture();

        Assert.Equal(expectedAssembly, fixture.Provider.GetRequiredService(service).GetType().Assembly.GetName().Name);
    }

    [Fact]
    public void Services_that_run_in_demo_do_not_reference_real_windows_assemblies()
    {
        var assemblies = new[]
        {
            typeof(ISubnetScanner).Assembly,
            typeof(SubnetScanner).Assembly,
            typeof(RemoteShadow.Demo.DemoData).Assembly,
            typeof(RemoteShadow.Modules.BuiltInModules).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name);
            Assert.DoesNotContain(references, r => RealAssemblies.Contains(r));
        }
    }
}
