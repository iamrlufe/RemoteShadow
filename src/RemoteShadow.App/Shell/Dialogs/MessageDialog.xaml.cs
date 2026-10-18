using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace RemoteShadow.App.Shell.Dialogs;

/// <summary>Простое окно сообщения: ошибка или информация.</summary>
public partial class MessageDialog : DialogWindowBase
{
    private readonly string _copyText;

    public MessageDialog(string title, string message, bool isError)
    {
        InitializeComponent();

        HeadingText.Text = title;
        MessageText.Text = message;
        IconText.Text = IconMap.Get(isError ? "circle-x" : "info-circle");
        IconText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");
        AutomationProperties.SetName(this, title);

        _copyText = string.IsNullOrWhiteSpace(message) ? title : title + Environment.NewLine + message;
        CopyButton.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
        FocusOnLoaded(OkButton);
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_copyText);
            CopyButton.Content = "Скопировано";
        }
        catch (Exception)
        {
            // Буфер обмена занят другим приложением: пользователь может повторить.
        }
    }
}
