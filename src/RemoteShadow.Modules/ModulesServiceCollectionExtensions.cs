using Microsoft.Extensions.DependencyInjection;

namespace RemoteShadow.Modules;

/// <summary>Подключение встроенных модулей к контейнеру зависимостей.</summary>
public static class ModulesServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует действия всех встроенных модулей (<see cref="BuiltInModules.All"/>). Страницы не регистрируются:
    /// их метаданные ведёт оболочка. Инфраструктура (индекс, журнал, настройки, запуск действий) — в другом проекте.
    /// </summary>
    public static IServiceCollection AddRemoteShadowModules(this IServiceCollection services)
    {
        foreach (var module in BuiltInModules.All)
            module.Register(services);

        return services;
    }
}
