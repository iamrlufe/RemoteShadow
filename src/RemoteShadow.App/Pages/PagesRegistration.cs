using Microsoft.Extensions.DependencyInjection;

namespace RemoteShadow.App.Pages;

public static class PagesRegistration
{
    /// <summary>Регистрирует встроенные страницы (<c>PageRegistration</c>) и их ViewModel. Заглушка: заполняется при реализации.</summary>
    public static IServiceCollection AddRemoteShadowPages(this IServiceCollection services)
    {
        return services;
    }
}
