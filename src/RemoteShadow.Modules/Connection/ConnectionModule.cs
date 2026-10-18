using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Подключение к сессиям и компьютерам: shadow, RDP, SSH, C$.</summary>
public sealed class ConnectionModule : IModule
{
    public const string GroupName = "Подключение";

    public string Id => "connection";
    public string Name => "Подключение";

    public void Register(IServiceCollection services)
    {
        services.AddAction<SessionTarget, ShadowViewAction>();
        services.AddAction<SessionTarget, ShadowControlAction>();
        services.AddAction<ComputerTarget, RdpAction>();
        services.AddAction<ComputerTarget, SshAction>();
        services.AddAction<ComputerTarget, OpenAdminShareAction>();
    }
}
