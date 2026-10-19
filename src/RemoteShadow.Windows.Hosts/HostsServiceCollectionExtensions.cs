using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Подключение к DI сервисов опроса компьютеров из этого проекта.</summary>
public static class HostsServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует сервисы опроса компьютеров: <see cref="ISessionService"/> (WTS), <see cref="IHostProbe"/> (TCP)
    /// и <see cref="IDiagnosticsService"/> (CIM). Им нужны <see cref="ICurrentConnection"/> и <see cref="ISettingsStore"/>,
    /// которые регистрируют другие проекты.
    /// </summary>
    public static IServiceCollection AddRemoteShadowWindowsHosts(this IServiceCollection services)
    {
        services.AddSingleton<ISessionService, WtsSessionService>();
        services.AddSingleton<IHostProbe, TcpHostProbe>();
        services.AddSingleton<IIcmpProbe, IcmpHostProbe>();
        services.AddSingleton<IArpResolver, ArpResolver>();
        services.AddSingleton<IDiagnosticsService, CimDiagnosticsService>();
        services.AddSingleton<IDeviceInventoryService, CimInventoryService>();
        return services;
    }
}
