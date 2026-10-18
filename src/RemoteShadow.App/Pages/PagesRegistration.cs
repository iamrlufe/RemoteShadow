using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.App.Shell;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.App.Pages;

/// <summary>
/// Регистрирует встроенные страницы (<see cref="PageRegistration"/>) и их ViewModel.
/// Каждая страница объединяет действия нескольких модулей для одного типа цели (см. <see cref="IActionTarget"/>):
/// «Компьютеры» — Connection и Power для <see cref="ComputerTarget"/>, Connection (shadow) и Sessions для
/// <see cref="SessionTarget"/>; «Пользователи AD» — модуль Users для <see cref="UserTarget"/>;
/// «Диагностика» — модуль Diagnostics для <see cref="DiagnosticFixTarget"/> (через <c>RemoteShadow.Modules</c>).
/// Сами действия регистрируются модулями (<c>AddRemoteShadowModules</c>); здесь — только страницы, которые их показывают.
/// </summary>
public static class PagesRegistration
{
    public static IServiceCollection AddRemoteShadowPages(this IServiceCollection services)
    {
        services.AddTransient<ComputersPageViewModel>();
        services.AddSingleton(new PageRegistration(
            new PageInfo(PageIds.Computers, "Компьютеры", "device-desktop", 0),
            sp => new ComputersPage { DataContext = sp.GetRequiredService<ComputersPageViewModel>() }));

        services.AddTransient<UsersPageViewModel>();
        services.AddSingleton(new PageRegistration(
            new PageInfo(PageIds.Users, "Пользователи AD", "users", 10),
            sp => new UsersPage { DataContext = sp.GetRequiredService<UsersPageViewModel>() }));

        services.AddTransient<DiagnosticsPageViewModel>();
        services.AddSingleton(new PageRegistration(
            new PageInfo(PageIds.Diagnostics, "Диагностика", "stethoscope", 20),
            sp => new DiagnosticsPage { DataContext = sp.GetRequiredService<DiagnosticsPageViewModel>() }));

        return services;
    }
}
