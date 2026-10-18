using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Actions.Tests;

public sealed class FakeDiagnosticsService : IDiagnosticsService
{
    public List<(ComputerInfo Computer, string FixId)> Fixes { get; } = new();

    public Task<DiagnosticReport> RunAsync(ComputerInfo computer, CancellationToken ct) =>
        Task.FromResult(new DiagnosticReport(computer.HostName, Array.Empty<DiagnosticItem>(), DateTime.UtcNow));

    public Task FixAsync(ComputerInfo computer, string fixId, CancellationToken ct)
    {
        Fixes.Add((computer, fixId));
        return Task.CompletedTask;
    }
}
