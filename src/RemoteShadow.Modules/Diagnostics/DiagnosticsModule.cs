using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.Modules;

/// <summary>Диагностика: исправление найденных проблем. Сама страница диагностики регистрируется оболочкой.</summary>
public sealed class DiagnosticsModule : IModule
{
    public const string GroupName = "Диагностика";

    public string Id => "diagnostics";
    public string Name => "Диагностика";

    public void Register(IServiceCollection services)
    {
        services.AddAction<DiagnosticFixTarget, ApplyDiagnosticFixAction>();
    }
}
