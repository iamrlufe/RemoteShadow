using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class ModuleRegistrationTests
{
    private static readonly string[] SessionActionIds =
    [
        "connection.shadow-view", "connection.shadow-control",
        "sessions.send-message", "sessions.disconnect", "sessions.logoff",
    ];

    private static readonly string[] ComputerActionIds =
    [
        "connection.rdp", "connection.ssh", "connection.open-admin-share",
        "power.reboot", "power.shutdown",
    ];

    private static readonly string[] UserActionIds =
    [
        "users.reset-password", "users.unlock", "users.enable", "users.disable",
        "users.add-to-group", "users.remove-from-group",
    ];

    private static readonly string[] DiagnosticActionIds = ["diagnostics.apply-fix"];

    private static readonly HashSet<string> AllowedIcons =
    [
        "device-desktop", "terminal-2", "folder", "eye", "mouse", "message", "plug-x", "logout", "refresh", "power",
        "player-stop", "key", "lock-open", "user-check", "user-off", "users-plus", "users-minus", "stethoscope", "tool",
    ];

    private static readonly HashSet<string> AllowedGroups =
        ["Подключение", "Сессия", "Компьютер", "Пользователь AD", "Диагностика"];

    private static ServiceCollection Registered()
    {
        var services = new ServiceCollection();
        services.AddRemoteShadowModules();
        return services;
    }

    private static List<IAction<TTarget>> ActionsFor<TTarget>(IServiceCollection services) where TTarget : IActionTarget
    {
        var provider = new SimpleServiceProvider();
        return services
            .Where(d => d.ServiceType == typeof(IAction<TTarget>))
            .Select(d =>
            {
                Assert.Equal(ServiceLifetime.Singleton, d.Lifetime);
                Assert.NotNull(d.ImplementationType);
                return (IAction<TTarget>)ActivatorUtilities.CreateInstance(provider, d.ImplementationType!);
            })
            .ToList();
    }

    [Fact]
    public void Built_in_modules_have_expected_unique_ids()
    {
        Assert.Equal(
            ["connection", "sessions", "power", "users", "diagnostics"],
            BuiltInModules.All.Select(m => m.Id));
        Assert.All(BuiltInModules.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
    }

    [Fact]
    public void Session_actions_are_registered()
    {
        var ids = ActionsFor<SessionTarget>(Registered()).Select(a => a.Id);
        Assert.Equal(SessionActionIds.Order(), ids.Order());
    }

    [Fact]
    public void Computer_actions_are_registered()
    {
        var ids = ActionsFor<ComputerTarget>(Registered()).Select(a => a.Id);
        Assert.Equal(ComputerActionIds.Order(), ids.Order());
    }

    [Fact]
    public void User_actions_are_registered()
    {
        var ids = ActionsFor<UserTarget>(Registered()).Select(a => a.Id);
        Assert.Equal(UserActionIds.Order(), ids.Order());
    }

    [Fact]
    public void Diagnostic_fix_action_is_registered()
    {
        var ids = ActionsFor<DiagnosticFixTarget>(Registered()).Select(a => a.Id);
        Assert.Equal(DiagnosticActionIds, ids);
    }

    [Fact]
    public void Action_ids_are_unique_across_all_targets()
    {
        var services = Registered();
        var ids = ActionsFor<SessionTarget>(services).Select(a => a.Id)
            .Concat(ActionsFor<ComputerTarget>(services).Select(a => a.Id))
            .Concat(ActionsFor<UserTarget>(services).Select(a => a.Id))
            .Concat(ActionsFor<DiagnosticFixTarget>(services).Select(a => a.Id))
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(SessionActionIds.Length + ComputerActionIds.Length + UserActionIds.Length + DiagnosticActionIds.Length, ids.Count);
    }

    [Fact]
    public void Each_action_type_is_registered_once()
    {
        var implementations = Registered()
            .Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IAction<>))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.Equal(implementations.Count, implementations.Distinct().Count());
    }

    [Fact]
    public void Only_actions_are_registered_pages_are_left_to_the_shell()
    {
        var services = Registered();

        Assert.All(services, d =>
        {
            Assert.True(d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IAction<>),
                $"Неожиданная регистрация: {d.ServiceType}");
        });
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPage));
    }

    [Fact]
    public void Action_ids_start_with_their_module_id()
    {
        foreach (var module in BuiltInModules.All)
        {
            var services = new ServiceCollection();
            module.Register(services);

            var ids = ActionsFor<SessionTarget>(services).Select(a => a.Id)
                .Concat(ActionsFor<ComputerTarget>(services).Select(a => a.Id))
                .Concat(ActionsFor<UserTarget>(services).Select(a => a.Id))
                .Concat(ActionsFor<DiagnosticFixTarget>(services).Select(a => a.Id))
                .ToList();

            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.StartsWith(module.Id + ".", id));
        }
    }

    [Fact]
    public void Action_metadata_is_filled_and_uses_known_icons_and_groups()
    {
        var services = Registered();
        var all = ActionsFor<SessionTarget>(services).Select(a => Describe(a))
            .Concat(ActionsFor<ComputerTarget>(services).Select(a => Describe(a)))
            .Concat(ActionsFor<UserTarget>(services).Select(a => Describe(a)))
            .Concat(ActionsFor<DiagnosticFixTarget>(services).Select(a => Describe(a)))
            .ToList();

        Assert.All(all, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Title), a.Id);
            Assert.Contains(a.Group, AllowedGroups);
            Assert.NotNull(a.Icon);
            Assert.Contains(a.Icon, AllowedIcons);
            Assert.False(string.IsNullOrWhiteSpace(a.Description), a.Id);
        });
    }

    [Fact]
    public void Order_is_unique_within_group_and_target()
    {
        var services = Registered();
        AssertOrderUnique(ActionsFor<SessionTarget>(services));
        AssertOrderUnique(ActionsFor<ComputerTarget>(services));
        AssertOrderUnique(ActionsFor<UserTarget>(services));
        AssertOrderUnique(ActionsFor<DiagnosticFixTarget>(services));
    }

    [Fact]
    public void Registering_returns_the_same_collection_for_chaining()
    {
        var services = new ServiceCollection();
        Assert.Same(services, services.AddRemoteShadowModules());
    }

    private static void AssertOrderUnique<T>(IEnumerable<IAction<T>> actions) where T : IActionTarget
    {
        foreach (var group in actions.GroupBy(a => a.Group))
        {
            var orders = group.Select(a => a.Order).ToList();
            Assert.Equal(orders.Count, orders.Distinct().Count());
        }
    }

    private static (string Id, string Title, string Group, string? Icon, string? Description) Describe<T>(IAction<T> a)
        where T : IActionTarget => (a.Id, a.Title, a.Group, a.Icon, a.Description);
}
