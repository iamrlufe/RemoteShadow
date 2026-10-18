using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>Запись о «запуске» программы: что бы запустилось в настоящем режиме.</summary>
public sealed record LaunchRecord(DateTime TimeUtc, string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Командная строка для показа: аргументы с пробелами в кавычках. Для чтения человеком, не для запуска.</summary>
    public string CommandLine =>
        string.Join(' ', new[] { FileName }.Concat(Arguments.Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a)));
}

/// <summary>
/// Ничего не запускает: записывает вызов в список и сообщает событием <see cref="Launched"/>.
/// Так демо-режим безопасно показывает, что сделали бы mstsc и ssh.
/// </summary>
public sealed class DemoProcessLauncher : IProcessLauncher
{
    private readonly object _gate = new();
    private readonly List<LaunchRecord> _launches = new();

    /// <summary>Вызывается синхронно из <see cref="StartAsync"/>, в потоке вызывающего.</summary>
    public event EventHandler<LaunchRecord>? Launched;

    /// <summary>Все «запуски» с начала работы, старые первыми.</summary>
    public IReadOnlyList<LaunchRecord> Launches
    {
        get { lock (_gate) return _launches.ToArray(); }
    }

    public Task StartAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ct.ThrowIfCancellationRequested();

        var record = new LaunchRecord(DateTime.UtcNow, fileName, arguments.ToArray());
        lock (_gate) _launches.Add(record);
        Launched?.Invoke(this, record);
        return Task.CompletedTask;
    }

    /// <summary>Вымышленный путь C:/Windows/System32/&lt;имя&gt;: программу найти «всегда можно».</summary>
    public string? FindExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var name = fileName.Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? null : $"C:/Windows/System32/{name}";
    }
}
