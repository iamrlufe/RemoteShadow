using System.Windows;
using RemoteShadow.App.Shell.Dialogs;
using RemoteShadow.Core;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Глобальные обработчики необработанных исключений (диспетчер интерфейса, задачи, домен приложения): запись в
/// <c>crash.log</c> и понятное окно вместо падения. В журнал и в окно попадают только тип, сообщение и стек:
/// пароли в исключения не кладут.
/// </summary>
internal sealed class UnhandledExceptionHandler
{
    private const int MaxErrorsInBurst = 5;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTime> _recent = new();
    private int _dialogOpen;

    public void Install(Application application)
    {
        application.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Handle("Интерфейс", e.Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Handle("Фоновая задача", e.Exception.Flatten().InnerException ?? e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is not Exception exception) return;

            if (e.IsTerminating) HandleFatal(exception);
            else Handle("Приложение", exception);
        };
    }

    private void Handle(string source, Exception exception)
    {
        CrashLog.Write(source, exception);

        var dispatcher = UiThread.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        // Окно показывается из потока интерфейса, где бы ни возникло исключение.
        dispatcher.BeginInvoke(() => ShowFriendlyDialog(exception));
    }

    // Процесс завершается сразу после обработчика: журнал и сообщение пишутся синхронно, окном Windows.
    private static void HandleFatal(Exception exception)
    {
        CrashLog.Write("Аварийное завершение", exception);

        try
        {
            MessageBox.Show(
                $"Программа аварийно завершает работу: {exception.Message}{Environment.NewLine}{Environment.NewLine}"
                + $"Подробности записаны в файл:{Environment.NewLine}{CrashLog.FilePath}",
                "RemoteShadow",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Показать сообщение не удалось: запись в журнале уже есть.
        }
    }

    private void ShowFriendlyDialog(Exception exception)
    {
        var now = DateTime.UtcNow;
        _recent.Enqueue(now);
        while (_recent.Count > 0 && now - _recent.Peek() > BurstWindow) _recent.Dequeue();
        var runaway = _recent.Count >= MaxErrorsInBurst;

        // Пока окно открыто, новые ошибки только пишутся в журнал: иначе окна пошли бы одно за другим.
        if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0) return;

        try
        {
            var (title, message) = Describe(exception, runaway);
            ShowMessage(title, message);
        }
        finally
        {
            Volatile.Write(ref _dialogOpen, 0);
        }

        if (runaway) Application.Current?.Shutdown(1);
    }

    /// <summary>Заголовок и текст окна: ожидаемая ошибка операции показывается как есть, остальные — с указанием на журнал.</summary>
    internal static (string Title, string Message) Describe(Exception exception, bool runaway)
    {
        if (exception is OperationFailedException && !runaway)
            return ("Операция не выполнена", exception.Message);

        var text = $"Произошла непредвиденная ошибка: {exception.Message}"
                   + Environment.NewLine + Environment.NewLine
                   + $"Подробности записаны в файл: {CrashLog.FilePath}";

        if (runaway)
        {
            text += Environment.NewLine + Environment.NewLine
                    + "Ошибки повторяются, поэтому программа будет закрыта. Запустите её снова.";
        }

        return ("Непредвиденная ошибка", text);
    }

    private static void ShowMessage(string title, string message)
    {
        try
        {
            var dialog = new MessageDialog(title, message, isError: true);
            DialogService.AttachOwner(dialog);
            dialog.ShowDialog();
        }
        catch (Exception)
        {
            // Не удалось показать своё окно (например, сбой темы): запасной вариант — окно Windows.
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
