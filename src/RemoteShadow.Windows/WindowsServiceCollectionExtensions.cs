using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows;

public static class WindowsServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует настоящие реализации сервисов Core для Windows (вход, LDAP, запуск процессов, Диспетчер учётных данных).
    /// <see cref="ICurrentConnection"/> здесь не регистрируется: его регистрирует App.
    /// </summary>
    public static IServiceCollection AddRemoteShadowWindows(this IServiceCollection services)
    {
        services.AddSingleton<IDomainLogonService, WindowsDomainLogonService>();
        services.AddSingleton<IComputerDirectory, LdapComputerDirectory>();
        services.AddSingleton<IUserDirectory, LdapUserDirectory>();
        services.AddSingleton<IProcessLauncher, WindowsProcessLauncher>();
        services.AddSingleton<ICredentialStore, CredentialManagerStore>();
        return services;
    }
}
