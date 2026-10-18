using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Питание компьютера: перезагрузка и выключение.</summary>
public sealed class ComputerPowerModule : IModule
{
    public const string GroupName = "Компьютер";

    public string Id => "power";
    public string Name => "Питание компьютера";

    public void Register(IServiceCollection services)
    {
        services.AddAction<ComputerTarget, RebootAction>();
        services.AddAction<ComputerTarget, ShutdownAction>();
    }
}
