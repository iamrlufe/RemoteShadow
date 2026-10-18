using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Actions.Tests;

public sealed class FakeSessionService : ISessionService
{
    public sealed record Message(string Host, int SessionId, string Title, string Text);
    public sealed record Shutdown(string Host, bool Reboot, TimeSpan Delay, string? Message);

    public List<Message> Messages { get; } = new();
    public List<(string Host, int SessionId)> Disconnects { get; } = new();
    public List<(string Host, int SessionId)> Logoffs { get; } = new();
    public List<Shutdown> Shutdowns { get; } = new();

    public int TotalCalls => Messages.Count + Disconnects.Count + Logoffs.Count + Shutdowns.Count;

    public Task<SessionListResult> GetSessionsAsync(string host, CancellationToken ct) =>
        Task.FromResult(SessionListResult.Ok(Array.Empty<SessionInfo>()));

    public Task SendMessageAsync(string host, int sessionId, string title, string text, CancellationToken ct)
    {
        Messages.Add(new Message(host, sessionId, title, text));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string host, int sessionId, CancellationToken ct)
    {
        Disconnects.Add((host, sessionId));
        return Task.CompletedTask;
    }

    public Task LogoffAsync(string host, int sessionId, CancellationToken ct)
    {
        Logoffs.Add((host, sessionId));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(string host, bool reboot, TimeSpan delay, string? message, CancellationToken ct)
    {
        Shutdowns.Add(new Shutdown(host, reboot, delay, message));
        return Task.CompletedTask;
    }
}
