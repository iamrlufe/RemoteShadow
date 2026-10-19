using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Services;
using RemoteShadow.Demo;
using RemoteShadow.Infrastructure;
using RemoteShadow.Modules;

namespace RemoteShadow.App.Tests;

/// <summary>
/// Собирает тот же стек, что и настоящая программа в демо-режиме (Infrastructure + Modules + Demo), но с
/// заглушками на месте дисковых и WPF-зависимостей (журнал, настройки, диалоги), чтобы действия модулей
/// выполнялись по-настоящему — как в приложении, а не имитировались в тесте.
/// </summary>
internal sealed class DemoFixture
{
    public DemoFixture(DemoData? data = null)
    {
        Data = data ?? DemoData.Generate(40, seed: 7, new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc));
        Notifications = new FakeNotificationService();
        Dialogs = new FakeDialogService();

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentConnection, CurrentConnection>();
        services.AddSingleton<IAuditLog>(new FakeAuditLog());
        services.AddSingleton<ISettingsStore>(new FakeSettingsStore());
        services.AddSingleton<IDialogService>(Dialogs);
        services.AddSingleton<INotificationService>(Notifications);
        services.AddRemoteShadowInfrastructure();
        services.AddRemoteShadowModules();
        services.AddRemoteShadowDemo(Data, DemoTimings.Default);

        Provider = services.BuildServiceProvider();
        Provider.GetRequiredService<ICurrentConnection>()
            .Set(new DemoDomainConnection(DemoData.DomainName, Data.DomainController, @"CORP\test-admin", usesExplicitCredentials: false));
    }

    public DemoData Data { get; }
    public ServiceProvider Provider { get; }
    public FakeNotificationService Notifications { get; }
    public FakeDialogService Dialogs { get; }

    public IActionRunner Runner => Provider.GetRequiredService<IActionRunner>();
    public ISessionService Sessions => Provider.GetRequiredService<ISessionService>();
    public IUserDirectory Users => Provider.GetRequiredService<IUserDirectory>();
    public IDiagnosticsService Diagnostics => Provider.GetRequiredService<IDiagnosticsService>();
    public ISettingsStore Settings => Provider.GetRequiredService<ISettingsStore>();
    public IComputerAvailabilityService Availability => Provider.GetRequiredService<IComputerAvailabilityService>();
    public ISubnetScanner Scanner => Provider.GetRequiredService<ISubnetScanner>();
    public IDeviceMergeService DeviceMerge => Provider.GetRequiredService<IDeviceMergeService>();
    public IComputerIndex Index => Provider.GetRequiredService<IComputerIndex>();

    /// <summary>
    /// Индекс, реально загруженный из демо-каталога (а не фейк) — нужен для тестов доступности/сканирования,
    /// где <see cref="IComputerAvailabilityService"/> и <see cref="IDeviceMergeService"/> должны видеть те же записи,
    /// что и ViewModel.
    /// </summary>
    public async Task<IComputerIndex> GetLoadedIndexAsync()
    {
        var index = Index;
        await index.RefreshAsync(full: true, CancellationToken.None);
        return index;
    }
}
