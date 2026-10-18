using System.Collections.ObjectModel;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Всплывающие уведомления в правом нижнем углу главного окна. Вызовы могут прийти из любого потока: коллекция
/// <see cref="Toasts"/> меняется только в потоке интерфейса.
/// </summary>
public sealed class NotificationService : INotificationService
{
    /// <summary>Сколько уведомлений видно одновременно; самые старые закрываются.</summary>
    public const int MaxVisible = 5;

    public ObservableCollection<ToastItem> Toasts { get; } = new();

    public void Info(string message) => Post(ToastKind.Info, message);

    public void Success(string message) => Post(ToastKind.Success, message);

    public void Error(string message) => Post(ToastKind.Error, message);

    private void Post(ToastKind kind, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var text = message.Trim();
        UiThread.Post(() => Add(kind, text));
    }

    private void Add(ToastKind kind, string message)
    {
        var toast = new ToastItem(kind, message, Dismiss);
        Toasts.Add(toast);
        toast.StartTimer();

        while (Toasts.Count > MaxVisible) Dismiss(Toasts[0]);
    }

    private void Dismiss(ToastItem toast)
    {
        toast.StopTimer();
        Toasts.Remove(toast);
    }
}
