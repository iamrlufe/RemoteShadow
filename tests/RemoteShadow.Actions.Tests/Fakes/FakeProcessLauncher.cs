using RemoteShadow.Core.Services;

namespace RemoteShadow.Actions.Tests;

/// <summary>Записывает запуски; какие программы «найдены», задаёт тест.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    public sealed record Launch(string FileName, IReadOnlyList<string> Arguments);

    public List<Launch> Launches { get; } = new();

    /// <summary>Имя программы → полный путь. Чего здесь нет, того «нет в системе».</summary>
    public Dictionary<string, string> Executables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task StartAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        Launches.Add(new Launch(fileName, arguments.ToArray()));
        return Task.CompletedTask;
    }

    public string? FindExecutable(string fileName) =>
        Executables.TryGetValue(fileName, out var path) ? path : null;
}
