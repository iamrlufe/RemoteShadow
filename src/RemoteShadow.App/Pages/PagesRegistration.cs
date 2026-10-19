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
            new PageInfo(PageIds.Computers, "Компьютеры", "device-desktop", 10),
            sp => new ComputersPage { DataContext = sp.GetRequiredService<ComputersPageViewModel>() }));

        services.AddTransient<UsersPageViewModel>();
        services.AddSingleton(new PageRegistration(
            new PageInfo(PageIds.Users, "Пользователи AD", "users", 20),
            sp => new UsersPage { DataContext = sp.GetRequiredService<UsersPageViewModel>() }));

        services.AddTransient<FavoritesPageViewModel>();
        services.AddSingleton(new PageRegistration(
            new PageInfo(PageIds.Favorites, "Избранное", "star", 30),
            sp => new FavoritesPage { DataContext = sp.GetRequiredService<FavoritesPageViewModel>() }));

        // Journal (Order 40) и Settings (Order 90) добавит следующий шаг по тому же образцу:
        //   services.AddTransient<JournalPageViewModel>();
        //   services.AddSingleton(new PageRegistration(
        //       new PageInfo(PageIds.Journal, "Журнал", "history", 40),
        //       sp => new JournalPage { DataContext = sp.GetRequiredService<JournalPageViewModel>() }));
        // Порядок в навигации: Computers 10, Users 20, Favorites 30, Journal 40, Diagnostics 50, Settings 90.

        services.AddTransient<DiagnosticsPageViewModel>();
        services.AddSingleton(new PageRegistration(
            new PageInfo(PageIds.Diagnostics, "Диагностика", "stethoscope", 50),
            sp => new DiagnosticsPage { DataContext = sp.GetRequiredService<DiagnosticsPageViewModel>() }));

        return services;
    }
}
