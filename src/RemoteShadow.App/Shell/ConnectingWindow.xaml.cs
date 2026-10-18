using System.ComponentModel;
using System.Windows;

namespace RemoteShadow.App.Shell;

/// <summary>Небольшое окно на время тихого входа текущим пользователем: контроллер домена может отвечать долго.</summary>
public partial class ConnectingWindow : Window
{
    public ConnectingWindow(string detail)
    {
        InitializeComponent();
        DetailText.Text = detail;
    }

    /// <summary>Пользователь отказался ждать (кнопка или закрытие окна).</summary>
    public event EventHandler? Skipped;

    private void OnSkipClick(object sender, RoutedEventArgs e) => Skipped?.Invoke(this, EventArgs.Empty);

    protected override void OnClosing(CancelEventArgs e)
    {
        Skipped?.Invoke(this, EventArgs.Empty);
        base.OnClosing(e);
    }
}
