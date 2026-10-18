using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Действия над сессиями пользователей: сообщение, отключение, выход из системы.</summary>
public sealed class SessionsModule : IModule
{
    public const string GroupName = "Сессия";

    public string Id => "sessions";
    public string Name => "Сессии";

    public void Register(IServiceCollection services)
    {
        services.AddAction<SessionTarget, SendMessageAction>();
        services.AddAction<SessionTarget, DisconnectSessionAction>();
        services.AddAction<SessionTarget, LogoffSessionAction>();
    }
}
