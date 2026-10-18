using System.Diagnostics;
using RemoteShadow.Core;

namespace ShadowSpike;

/// <summary>Журнал одного шага: итог, пояснение и подробности. Всё, что сюда пишется, проходит через вычистку секретов.</summary>
internal sealed class StepLog
{
    private readonly Func<string, string> _redact;
    private readonly HashSet<string> _triggers;

    public StepLog(Func<string, string> redact, HashSet<string> triggers)
    {
        _redact = redact;
        _triggers = triggers;
    }

    public StepStatus Status { get; private set; } = StepStatus.Pass;
    public string Summary { get; private set; } = string.Empty;
    public List<string> Details { get; } = [];

    public void Info(string line) => Details.Add(_redact(line));

    public void Warn(string line) => Details.Add(_redact("ВНИМАНИЕ: " + line));

    public void Trigger(string key) => _triggers.Add(key);

    public void Pass(string summary) => Set(StepStatus.Pass, summary);

    public void Fail(string summary) => Set(StepStatus.Fail, summary);

    public void Skip(string summary) => Set(StepStatus.Skip, summary);

    private void Set(StepStatus status, string summary)
    {
        Status = status;
        Summary = _redact(summary);
    }
}

/// <summary>Выполняет шаги: меряет время, ловит любые исключения и печатает ход выполнения.</summary>
internal sealed class StepRunner
{
    private readonly Func<string, string> _redact;
    private readonly HashSet<string> _triggers;

    public StepRunner(Func<string, string> redact, HashSet<string> triggers)
    {
        _redact = redact;
        _triggers = triggers;
    }

    public List<StepResult> Results { get; } = [];

    public async Task<StepResult> RunAsync(string id, string name, Func<StepLog, Task> body)
    {
        Console.WriteLine();
        Console.WriteLine($"[{id}] {name}...");
        var log = new StepLog(_redact, _triggers);
        var sw = Stopwatch.StartNew();
        try
        {
            await body(log);
            if (log.Summary.Length == 0) log.Pass("выполнено");
        }
        catch (OperationFailedException ex)
        {
            if (ex.Failure == OperationFailure.SecondaryLogonUnavailable) log.Trigger("seclogon");
            log.Info($"Исключение OperationFailedException ({ex.Failure}): {ex.Message}");
            if (ex.InnerException is not null) log.Info($"Внутреннее: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            log.Fail($"{ex.Failure}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            log.Fail("операция отменена по таймауту шага");
        }
        catch (Exception ex)
        {
            log.Info($"Исключение {ex.GetType().FullName}: {ex.Message}");
            if (ex.InnerException is not null) log.Info($"Внутреннее: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            log.Fail($"{ex.GetType().Name}: {ex.Message}");
        }

        sw.Stop();
        var result = new StepResult(id, name, log.Status, sw.ElapsedMilliseconds, log.Summary, log.Details);
        Results.Add(result);
        foreach (var d in log.Details) Console.WriteLine("    " + d);
        Console.WriteLine($"  => {ReportBuilder.StatusText(result.Status)} ({result.ElapsedMs} мс): {result.Summary}");
        return result;
    }

    /// <summary>Шаг, который не выполняется: фиксируется как SKIP.</summary>
    public Task<StepResult> SkipAsync(string id, string name, string reason) =>
        RunAsync(id, name, log =>
        {
            log.Skip(reason);
            return Task.CompletedTask;
        });
}
