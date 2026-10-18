using System.Windows;
using RemoteShadow.App.Shell.Dialogs;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Модальные окна поверх активного окна. Методы можно вызывать из любого потока: окно открывается в потоке
/// интерфейса, а вызывающий получает результат, когда окно закрыто.
/// </summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(ConfirmRequest request) =>
        OnUiThread(() =>
        {
            var dialog = new ConfirmDialog(request, withReason: false);
            AttachOwner(dialog);
            return dialog.ShowDialog() == true && dialog.Confirmed;
        });

    public Task<ReasonResult> ConfirmWithReasonAsync(ConfirmRequest request) =>
        OnUiThread(() =>
        {
            var dialog = new ConfirmDialog(request, withReason: true);
            AttachOwner(dialog);
            var confirmed = dialog.ShowDialog() == true && dialog.Confirmed;
            return confirmed ? new ReasonResult(true, dialog.Reason) : new ReasonResult(false, null);
        });

    public Task<string?> PromptTextAsync(string title, string message, string? initial = null, bool multiline = false) =>
        OnUiThread<string?>(() =>
        {
            var dialog = new PromptTextDialog(title, message, initial, multiline);
            AttachOwner(dialog);
            return dialog.ShowDialog() == true ? dialog.Result : null;
        });

    public Task<PasswordChoice?> PromptPasswordResetAsync(string userLabel) =>
        OnUiThread<PasswordChoice?>(() =>
        {
            var dialog = new PasswordResetDialog(userLabel);
            AttachOwner(dialog);
            return dialog.ShowDialog() == true ? dialog.Result : null;
        });

    public Task<GroupInfo?> PickGroupAsync(
        string title,
        Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>> search) =>
        OnUiThread<GroupInfo?>(() =>
        {
            var dialog = new PickGroupDialog(title, search);
            AttachOwner(dialog);
            return dialog.ShowDialog() == true ? dialog.Result : null;
        });

    public Task ShowErrorAsync(string title, string message) =>
        OnUiThread(() =>
        {
            Show(new MessageDialog(title, message, isError: true));
            return true;
        });

    public Task ShowInfoAsync(string title, string message) =>
        OnUiThread(() =>
        {
            Show(new MessageDialog(title, message, isError: false));
            return true;
        });

    /// <summary>
    /// Делает активное окно владельцем диалога (иначе главное; если окон нет — по центру экрана).
    /// Нужен и тем, кто показывает свои окна: например, обработчику необработанных исключений.
    /// </summary>
    internal static void AttachOwner(Window dialog)
    {
        var owner = FindOwner(dialog);
        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        dialog.Owner = owner;
    }

    private static void Show(Window dialog)
    {
        AttachOwner(dialog);
        dialog.ShowDialog();
    }

    private static Window? FindOwner(Window dialog)
    {
        var application = Application.Current;
        if (application is null) return null;

        foreach (Window window in application.Windows)
        {
            if (!ReferenceEquals(window, dialog) && window.IsActive && window.IsVisible) return window;
        }

        var main = application.MainWindow;
        return main is { IsVisible: true } && !ReferenceEquals(main, dialog) ? main : null;
    }

    private static Task<T> OnUiThread<T>(Func<T> show)
    {
        var dispatcher = UiThread.Dispatcher
                         ?? throw new InvalidOperationException("Приложение уже завершено.");

        if (!dispatcher.CheckAccess()) return dispatcher.InvokeAsync(show).Task;

        try
        {
            return Task.FromResult(show());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}
