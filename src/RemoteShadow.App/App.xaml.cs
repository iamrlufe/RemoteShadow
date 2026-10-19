using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.App.Pages;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Dialogs;
using RemoteShadow.App.Shell.Login;
using RemoteShadow.App.Shell.Search;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Demo;
using RemoteShadow.Infrastructure;
using RemoteShadow.Modules;
using RemoteShadow.Windows;
using RemoteShadow.Windows.Hosts;

namespace RemoteShadow.App;

/// <summary>
/// Composition root: разбор аргументов, сборка контейнера, вход в домен, главное окно. Приложение закрывается вместе
/// с главным окном; до его появления (окно входа) завершение запрашивается явно.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    private SingleInstanceGuard? _instanceGuard;

    public App()
    {
        new UnhandledExceptionHandler().Install(this);
        EnableBindingTrace();
    }

    /// <summary>
    /// Отладочная сборка: при <c>REMOTESHADOW_TRACE_BINDINGS=1</c> ошибки привязок WPF пишутся в
    /// <c>%TEMP%\RemoteShadow-bindings.log</c> — их видно и без отладчика (например, при проверке скриншотами).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void EnableBindingTrace()
    {
        if (Environment.GetEnvironmentVariable("REMOTESHADOW_TRACE_BINDINGS") != "1") return;

        var path = Path.Combine(Path.GetTempPath(), "RemoteShadow-bindings.log");
        var listener = new System.Diagnostics.TextWriterTraceListener(new StreamWriter(path, append: false) { AutoFlush = true });
        // Refresh перечитывает конфигурацию и сбрасывает слушателей, поэтому слушатель добавляется после него.
        System.Diagnostics.PresentationTraceSources.Refresh();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(listener);
        source.Switch.Level = System.Diagnostics.SourceLevels.Warning;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Значок приложения во всех окнах (заголовок, Alt+Tab): у окон без явного Icon он иначе общий системный.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(static (sender, _) =>
        {
            if (sender is Window { Icon: null } window && AppIcon.Image is { } icon) window.Icon = icon;
        }));

        try
        {
            await StartAsync(e.Args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Запуск программы", ex);
            MessageBox.Show(
                $"Не удалось запустить программу: {ex.Message}{Environment.NewLine}{Environment.NewLine}"
                + $"Подробности записаны в файл:{Environment.NewLine}{CrashLog.FilePath}",
                "RemoteShadow",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SecureClipboard.ClearPending();

        try
        {
            if (_services is not null)
            {
                _services.GetRequiredService<ConnectionCoordinator>().Shutdown();

                // Освобождает подключение (и сохранённый в памяти пароль).
                _services.GetRequiredService<ICurrentConnection>().Set(null);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Завершение программы", ex);
        }

        _services?.Dispose();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    private async Task StartAsync(IEnumerable<string> args)
    {
        var options = StartupOptions.Parse(args, Environment.GetEnvironmentVariable);

        // Демо-режим хранит всё во временной папке и не трогает настройки и журнал настоящей работы.
        var paths = options.IsDemo
            ? new AppPaths(Path.Combine(Path.GetTempPath(), "RemoteShadow-Demo"))
            : options.Profile is { } profile
                ? new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteShadow-" + profile))
                : new AppPaths();
        CrashLog.Initialize(paths.DataDir);

        // Два экземпляра с общим settings.json затирают правки друг друга.
        _instanceGuard = SingleInstanceGuard.TryAcquire(paths.DataDir);
        if (_instanceGuard is null)
        {
            MessageBox.Show(
                "RemoteShadow уже запущен. Закройте другое окно или используйте его.",
                "RemoteShadow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var services = BuildServices(options, paths);
        _services = services;

        var settings = LoadSettings(services);
        services.GetRequiredService<ThemeService>().Apply(settings.Theme);
        ShowDemoLaunches(services, options);

        var coordinator = services.GetRequiredService<ConnectionCoordinator>();
        var signedIn = (!options.PromptsForLogin && await coordinator.TryAutoLogonAsync(settings)) || coordinator.ShowLogin(null, exitsApp: true);
        if (!signedIn)
        {
            Shutdown();
            return;
        }

        var window = services.GetRequiredService<MainWindow>();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static ServiceProvider BuildServices(StartupOptions options, AppPaths paths)
    {
        var services = new ServiceCollection();

        // AppPaths регистрируется раньше инфраструктуры: она добавляет свой экземпляр через TryAdd.
        services.AddSingleton(options);
        services.AddSingleton(paths);
        services.AddSingleton<ICurrentConnection, CurrentConnection>();

        services.AddRemoteShadowInfrastructure();
        services.AddRemoteShadowModules();

        if (options.IsDemo) services.AddRemoteShadowDemo();
        else services.AddRemoteShadowWindows().AddRemoteShadowWindowsHosts();

        services.AddRemoteShadowPages();
        services.AddShell();

#if DEBUG
        // В отладочной сборке отсутствующая регистрация находится при запуске, а не при первом открытии страницы.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
#else
        return services.BuildServiceProvider();
#endif
    }

    private static AppSettings LoadSettings(IServiceProvider services)
    {
        try
        {
            var settings = services.GetRequiredService<ISettingsStore>().Load();

            // Демо-режим: наполняем избранное/группы/подсети демо-данными, чтобы фичу можно было увидеть сразу,
            // без ручной настройки (только если пользователь ещё ничего не настроил в этой демо-сессии).
            if (services.GetService<DemoData>() is { } demoData)
            {
                DemoSettingsSeeding.SeedIfEmpty(settings, demoData);
                services.GetRequiredService<ISettingsStore>().Save(settings);
            }

            return settings;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Чтение настроек", ex);
            return new AppSettings();
        }
    }

    // Демо-режим ничего не запускает: показываем, какая команда была бы выполнена.
    private static void ShowDemoLaunches(IServiceProvider services, StartupOptions options)
    {
        if (!options.IsDemo) return;

        var notifications = services.GetRequiredService<INotificationService>();
        if (services.GetService<DemoProcessLauncher>() is { } launcher)
            launcher.Launched += (_, launch) => notifications.Info($"Демо: программа не запускается. Была бы выполнена команда: {launch.CommandLine}");
    }
}

/// <summary>Регистрация сервисов оболочки: диалоги, уведомления, навигация, вход, главное окно.</summary>
internal static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());

        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

        services.AddSingleton<ThemeService>();
        services.AddSingleton<WindowStateStore>();
        services.AddSingleton<ConnectionCoordinator>();
        services.AddTransient<LoginViewModel>();

        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
