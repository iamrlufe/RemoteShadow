using System.Diagnostics;

namespace RemoteShadow.Infrastructure.Tests;

/// <summary>Ожидание условий в тестах: ограниченное по времени, без бесконечных зависаний.</summary>
public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 10_000, string? what = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Условие не выполнилось за {timeoutMs} мс" + (what is null ? "." : $": {what}."));

            await Task.Delay(5);
        }
    }

    /// <summary>Проверяет, что условие остаётся истинным в течение <paramref name="ms"/> миллисекунд (например, «пятая проверка не стартовала»).</summary>
    public static async Task HoldsAsync(Func<bool> condition, int ms = 150, string? what = null)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            if (!condition()) throw new Xunit.Sdk.XunitException("Условие нарушено" + (what is null ? "." : $": {what}."));
            await Task.Delay(5);
        }
        while (watch.ElapsedMilliseconds < ms);
    }

    /// <summary>Ждёт задачу не дольше таймаута (чтобы тест не завис, если код под тестом заблокирован).</summary>
    public static async Task WithTimeout(this Task task, int timeoutMs = 10_000)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Задача не завершилась за {timeoutMs} мс.");
        }
    }

    public static async Task<T> WithTimeout<T>(this Task<T> task, int timeoutMs = 10_000)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Задача не завершилась за {timeoutMs} мс.");
        }
    }
}

/// <summary>Задвижка: все, кто в неё вошёл, ждут, пока тест не вызовет <see cref="Open"/>.</summary>
public sealed class Gate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen => _open.Task.IsCompleted;

    public Task WaitAsync(CancellationToken ct = default) => _open.Task.WaitAsync(ct);

    public void Open() => _open.TrySetResult();
}

/// <summary>Прогресс, который записывает отчёты сразу, в потоке вызывающего (в отличие от <see cref="Progress{T}"/>).</summary>
public sealed class ListProgress<T> : IProgress<T>
{
    private readonly List<T> _items = new();

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_items) return _items.ToList();
        }
    }

    public void Report(T value)
    {
        lock (_items) _items.Add(value);
    }
}
