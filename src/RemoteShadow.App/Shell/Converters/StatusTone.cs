using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Shell.Converters;

/// <summary>Смысловой цвет статуса. Один и тот же для точки, бейджа и текста.</summary>
public enum StatusTone
{
    /// <summary>Нет данных или не проверялось.</summary>
    Muted,

    /// <summary>Нейтральное состояние: не в сети, простаивает.</summary>
    Neutral,

    Success,
    Warning,
    Critical,
    Info,
}

/// <summary>Сопоставляет значения моделей Core (bool?, <see cref="DiagnosticStatus"/>, <see cref="SessionState"/> и т. д.) с <see cref="StatusTone"/>.</summary>
public static class StatusToneMap
{
    public static StatusTone From(object? value) => value switch
    {
        null => StatusTone.Muted,
        bool b => b ? StatusTone.Success : StatusTone.Neutral,
        StatusTone tone => tone,
        DiagnosticStatus s => s switch
        {
            DiagnosticStatus.Ok => StatusTone.Success,
            DiagnosticStatus.Warning => StatusTone.Warning,
            DiagnosticStatus.Fail => StatusTone.Critical,
            _ => StatusTone.Muted,
        },
        SessionState s => s switch
        {
            SessionState.Active => StatusTone.Success,
            SessionState.Disconnected => StatusTone.Warning,
            SessionState.Shadow or SessionState.Connected or SessionState.ConnectQuery or SessionState.Init => StatusTone.Info,
            SessionState.Idle or SessionState.Listen or SessionState.Reset or SessionState.Down => StatusTone.Neutral,
            _ => StatusTone.Muted,
        },
        ActionStatus s => s switch
        {
            ActionStatus.Ok => StatusTone.Success,
            ActionStatus.Failed => StatusTone.Critical,
            _ => StatusTone.Muted,
        },
        HostQueryStatus s => s switch
        {
            HostQueryStatus.Ok => StatusTone.Success,
            HostQueryStatus.Unreachable or HostQueryStatus.Timeout => StatusTone.Warning,
            _ => StatusTone.Critical,
        },
        string text => FromText(text),
        _ => StatusTone.Muted,
    };

    private static StatusTone FromText(string text) => text.Trim().ToLowerInvariant() switch
    {
        "success" or "ok" or "online" => StatusTone.Success,
        "warning" or "caution" => StatusTone.Warning,
        "error" or "fail" or "failed" or "critical" or "danger" => StatusTone.Critical,
        "info" or "attention" => StatusTone.Info,
        "neutral" or "offline" => StatusTone.Neutral,
        _ => StatusTone.Muted,
    };
}
