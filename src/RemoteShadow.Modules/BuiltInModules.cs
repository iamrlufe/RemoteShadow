using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Встроенные модули программы.</summary>
public static class BuiltInModules
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        new ConnectionModule(),
        new SessionsModule(),
        new ComputerPowerModule(),
        new UsersModule(),
        new DiagnosticsModule(),
    ];
}
