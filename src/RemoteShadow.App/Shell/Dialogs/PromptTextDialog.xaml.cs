using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace RemoteShadow.App.Shell.Dialogs;

/// <summary>Ввод текста: однострочный (Enter подтверждает) или многострочный (Enter — новая строка, Ctrl+Enter подтверждает).</summary>
public partial class PromptTextDialog : DialogWindowBase
{
    public PromptTextDialog(string title, string message, string? initial, bool multiline)
    {
        InitializeComponent();

        HeadingText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(InputBox, title);

        if (multiline)
        {
            InputBox.AcceptsReturn = true;
            InputBox.TextWrapping = TextWrapping.Wrap;
            InputBox.MinHeight = 110;
            InputBox.MaxHeight = 260;
            InputBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            MultilineHint.Visibility = Visibility.Visible;
            InputBox.PreviewKeyDown += OnMultilineKeyDown;
        }

        InputBox.Text = initial ?? string.Empty;
        InputBox.SelectAll();
        OkButton.IsEnabled = HasText;
        FocusOnLoaded(InputBox);
    }

    /// <summary>Введённый текст; null, если пользователь отменил.</summary>
    public string? Result { get; private set; }

    private bool HasText => !string.IsNullOrWhiteSpace(InputBox.Text);

    private void OnInputChanged(object sender, TextChangedEventArgs e) => OkButton.IsEnabled = HasText;

    private void OnMultilineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Accept();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (!HasText) return;
        Result = InputBox.Text;
        DialogResult = true;
    }
}
