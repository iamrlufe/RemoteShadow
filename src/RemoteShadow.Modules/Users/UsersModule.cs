using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Действия над пользователями AD: пароль, блокировка, включение, группы.</summary>
public sealed class UsersModule : IModule
{
    public const string GroupName = "Пользователь AD";

    public string Id => "users";
    public string Name => "Пользователи AD";

    public void Register(IServiceCollection services)
    {
        services.AddAction<UserTarget, ResetPasswordAction>();
        services.AddAction<UserTarget, UnlockAction>();
        services.AddAction<UserTarget, EnableAction>();
        services.AddAction<UserTarget, DisableAction>();
        services.AddAction<UserTarget, AddToGroupAction>();
        services.AddAction<UserTarget, RemoveFromGroupAction>();
    }
}
