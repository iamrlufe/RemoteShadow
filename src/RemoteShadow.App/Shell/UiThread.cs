using System.Windows;
using System.Windows.Threading;

namespace RemoteShadow.App.Shell;

/// <summary>Маршалинг в поток интерфейса: сервисы Core могут вызываться из любого потока.</summary>
internal static class UiThread
{
    /// <summary>Диспетчер приложения или null, если приложение уже завершилось.</summary>
    public static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    /// <summary>Выполняет действие в потоке интерфейса без ожидания. После завершения приложения ничего не делает.</summary>
    public static void Post(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action);
    }

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var dispatcher = Dispatcher ?? throw new InvalidOperationException("Приложение уже завершено.");
        return dispatcher.InvokeAsync(func).Task;
    }

    public static Task InvokeAsync(Action action)
    {
        var dispatcher = Dispatcher ?? throw new InvalidOperationException("Приложение уже завершено.");
        return dispatcher.InvokeAsync(action).Task;
    }

    public static Task<T> InvokeTaskAsync<T>(Func<Task<T>> func)
    {
        var dispatcher = Dispatcher ?? throw new InvalidOperationException("Приложение уже завершено.");
        return dispatcher.InvokeAsync(func).Task.Unwrap();
    }
}
