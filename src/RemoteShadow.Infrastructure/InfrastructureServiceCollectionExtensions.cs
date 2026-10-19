using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует сервисы-реализации Core, не зависящие от Windows: индекс компьютеров, статус «в сети», кэш сессий,
    /// поиск пользователя, журнал, настройки, запуск действий. <see cref="ICurrentConnection"/> здесь не регистрируется — его
    /// регистрирует App. Уже зарегистрированные сервисы не заменяются (например, <see cref="AppPaths"/> с другим корнем для демо-режима).
    /// </summary>
    public static IServiceCollection AddRemoteShadowInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new AppPaths());

        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton<IAuditLog, JsonAuditLog>();
        services.TryAddSingleton<ISessionCache, SessionCache>();
        services.TryAddSingleton<IComputerIndex, ComputerIndex>();
        services.TryAddSingleton<IOnlineStatusService, OnlineStatusService>();
        services.TryAddSingleton<IUserLocator, UserLocator>();
        services.TryAddSingleton<IActionRunner, ActionRunner>();

        services.TryAddSingleton<IDnsResolverService, DnsResolverService>();
        services.TryAddSingleton<IReverseDnsResolver, ReverseDnsResolver>();
        services.TryAddSingleton<IComputerAvailabilityService, ComputerAvailabilityService>();
        services.TryAddSingleton<ISubnetScanner, SubnetScanner>();
        services.TryAddSingleton<IDeviceMergeService, DeviceMergeService>();

        return services;
    }
}
