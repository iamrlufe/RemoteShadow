using System.Windows;
using System.Windows.Controls;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.App.Shell.Dialogs;

/// <summary>
/// Сброс пароля: сгенерировать (показать, скопировать) или ввести вручную с подтверждением.
/// Пароль нигде не логируется; поля и сгенерированное значение очищаются при закрытии окна.
/// </summary>
public partial class PasswordResetDialog : DialogWindowBase
{
    private const int GeneratedLength = 14;
    private static readonly TimeSpan ClipboardLifetime = TimeSpan.FromSeconds(60);

    private string? _generated;
    private bool _revealed;

    public PasswordResetDialog(string userLabel)
    {
        InitializeComponent();

        UserText.Text = $"Пользователь: {userLabel}";
        Generate();
        UpdateResetEnabled();
        FocusOnLoaded(ResetButton);
    }

    /// <summary>Выбранный пароль; null, если пользователь отменил.</summary>
    public PasswordChoice? Result { get; private set; }

    private bool IsGenerateMode => GenerateRadio.IsChecked == true;

    private void Generate()
    {
        _generated = PasswordGenerator.Generate(GeneratedLength);
        CopyButton.ToolTip = "Копировать (буфер обмена очистится через 60 секунд)";
        ShowGenerated();
    }

    private void ShowGenerated()
    {
        GeneratedBox.Text = _generated is null
            ? string.Empty
            : _revealed ? _generated : new string('•', _generated.Length);

        RevealButton.Content = _revealed ? "" : "";
        var hint = _revealed ? "Скрыть пароль" : "Показать пароль";
        RevealButton.ToolTip = hint;
        System.Windows.Automation.AutomationProperties.SetName(RevealButton, hint);
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        // Событие приходит и во время InitializeComponent, когда часть элементов ещё не создана.
        if (!IsLoadedInternally) return;

        var generate = IsGenerateMode;
        GeneratePanel.Visibility = generate ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = generate ? Visibility.Collapsed : Visibility.Visible;
        if (!generate) NewPasswordBox.Focus();
        UpdateResetEnabled();
    }

    // true, когда InitializeComponent уже создал все элементы.
    private bool IsLoadedInternally => ManualPanel is not null && ResetButton is not null && NewPasswordBox is not null;

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        ShowGenerated();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_generated is null) return;

        var copied = SecureClipboard.CopyWithAutoClear(_generated, ClipboardLifetime);
        CopyButton.ToolTip = copied
            ? "Скопировано. Буфер обмена очистится через 60 секунд"
            : "Не удалось скопировать: буфер обмена занят другой программой";
    }

    private void OnRegenerateClick(object sender, RoutedEventArgs e) => Generate();

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdateResetEnabled();

    private void UpdateResetEnabled()
    {
        if (!IsLoadedInternally) return;

        if (IsGenerateMode)
        {
            ResetButton.IsEnabled = !string.IsNullOrEmpty(_generated);
            return;
        }

        var password = NewPasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;
        var mismatch = confirm.Length > 0 && password != confirm;
        MismatchText.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.IsEnabled = password.Length > 0 && password == confirm;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var password = IsGenerateMode ? _generated : NewPasswordBox.Password;
        if (string.IsNullOrEmpty(password)) return;
        if (!IsGenerateMode && password != ConfirmPasswordBox.Password) return;

        Result = new PasswordChoice(password, MustChangeBox.IsChecked == true);
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _generated = null;
        GeneratedBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        base.OnClosed(e);
    }
}
