using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows.Hosts.Tests;

public class ServiceRegistrationTests
{
    [Fact]
    public void Registers_six_singletons_and_nothing_else()
    {
        var services = new ServiceCollection();

        var returned = services.AddRemoteShadowWindowsHosts();

        Assert.Same(services, returned);
        Assert.Collection(
            services.OrderBy(d => d.ServiceType.Name),
            d => Assert.Equal((typeof(IArpResolver), typeof(ArpResolver)), (d.ServiceType, d.ImplementationType)),
            d => Assert.Equal((typeof(IDeviceInventoryService), typeof(CimInventoryService)), (d.ServiceType, d.ImplementationType)),
            d => Assert.Equal((typeof(IDiagnosticsService), typeof(CimDiagnosticsService)), (d.ServiceType, d.ImplementationType)),
            d => Assert.Equal((typeof(IHostProbe), typeof(TcpHostProbe)), (d.ServiceType, d.ImplementationType)),
            d => Assert.Equal((typeof(IIcmpProbe), typeof(IcmpHostProbe)), (d.ServiceType, d.ImplementationType)),
            d => Assert.Equal((typeof(ISessionService), typeof(WtsSessionService)), (d.ServiceType, d.ImplementationType)));
        Assert.All(services, d => Assert.Equal(ServiceLifetime.Singleton, d.Lifetime));
    }

    [Fact]
    public void Implementations_have_a_single_public_constructor_so_di_can_pick_it()
    {
        foreach (var type in new[] { typeof(WtsSessionService), typeof(CimDiagnosticsService), typeof(TcpHostProbe), typeof(IcmpHostProbe), typeof(ArpResolver), typeof(CimInventoryService) })
            Assert.Single(type.GetConstructors());
    }
}
