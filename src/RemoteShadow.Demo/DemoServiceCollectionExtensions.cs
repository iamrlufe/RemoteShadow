using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>Подключение демо-режима (--demo) к контейнеру зависимостей.</summary>
public static class DemoServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует тестовые реализации сервисов Core (режим --demo): данные <see cref="DemoData.CreateDefault"/> и все
    /// сервисы как singleton, под интерфейсами Core и под собственными типами (чтобы оболочка могла подписаться,
    /// например, на <see cref="DemoProcessLauncher.Launched"/>). <see cref="ICurrentConnection"/> не регистрируется: это дело оболочки.
    /// </summary>
    public static IServiceCollection AddRemoteShadowDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => DemoData.CreateDefault());
        services.AddSingleton(_ => DemoTimings.Default);
        return services.AddDemoServices();
    }

    /// <summary>То же, но с готовыми данными и задержками (для тестов и нестандартных демо-наборов).</summary>
    public static IServiceCollection AddRemoteShadowDemo(this IServiceCollection services, DemoData data, DemoTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(data);

        services.AddSingleton(data);
        services.AddSingleton(timings ?? DemoTimings.Default);
        return services.AddDemoServices();
    }

    private static IServiceCollection AddDemoServices(this IServiceCollection services)
    {
        services.AddSingleton(sp => new DemoComputerDirectory(sp.GetRequiredService<DemoData>(), sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<IComputerDirectory>(sp => sp.GetRequiredService<DemoComputerDirectory>());

        services.AddSingleton(sp => new DemoUserDirectory(sp.GetRequiredService<DemoData>()));
        services.AddSingleton<IUserDirectory>(sp => sp.GetRequiredService<DemoUserDirectory>());

        services.AddSingleton(sp => new DemoSessionService(sp.GetRequiredService<DemoData>(), sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<ISessionService>(sp => sp.GetRequiredService<DemoSessionService>());

        services.AddSingleton(sp => new DemoHostProbe(sp.GetRequiredService<DemoData>(), sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<IHostProbe>(sp => sp.GetRequiredService<DemoHostProbe>());

        services.AddSingleton(_ => new DemoNetworkMap());
        services.AddSingleton(sp => new DemoIcmpProbe(sp.GetRequiredService<DemoData>(), sp.GetRequiredService<DemoNetworkMap>(), sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<IIcmpProbe>(sp => sp.GetRequiredService<DemoIcmpProbe>());

        services.AddSingleton(sp => new DemoDnsResolverService(sp.GetRequiredService<DemoData>(), sp.GetRequiredService<DemoNetworkMap>(), sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<IDnsResolverService>(sp => sp.GetRequiredService<DemoDnsResolverService>());

        services.AddSingleton(sp => new DemoReverseDnsResolver(sp.GetRequiredService<DemoNetworkMap>(), sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<IReverseDnsResolver>(sp => sp.GetRequiredService<DemoReverseDnsResolver>());

        services.AddSingleton(sp => new DemoSubnetScanner(
            sp.GetRequiredService<DemoData>(), sp.GetRequiredService<DemoNetworkMap>(), sp.GetService<TimeProvider>() ?? TimeProvider.System, sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<ISubnetScanner>(sp => sp.GetRequiredService<DemoSubnetScanner>());

        services.AddSingleton<IDeviceInventoryService, DemoDeviceInventoryService>();

        services.AddSingleton(_ => new DemoProcessLauncher());
        services.AddSingleton<IProcessLauncher>(sp => sp.GetRequiredService<DemoProcessLauncher>());

        services.AddSingleton(sp => new DemoDiagnosticsService(sp.GetRequiredService<DemoData>(), sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<IDiagnosticsService>(sp => sp.GetRequiredService<DemoDiagnosticsService>());

        services.AddSingleton(_ => new DemoCredentialStore());
        services.AddSingleton<ICredentialStore>(sp => sp.GetRequiredService<DemoCredentialStore>());

        services.AddSingleton(sp => new DemoDomainLogonService(timings: sp.GetRequiredService<DemoTimings>()));
        services.AddSingleton<IDomainLogonService>(sp => sp.GetRequiredService<DemoDomainLogonService>());

        return services;
    }
}
