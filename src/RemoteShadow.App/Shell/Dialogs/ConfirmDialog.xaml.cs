using System.Windows;
using System.Windows.Controls;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell.Dialogs;

/// <summary>
/// Подтверждение «Да/Нет» с перечнем целей; с <c>withReason</c> — ещё и обязательное поле «Причина».
/// Для опасного действия фокус стоит на «Отмена», чтобы случайный Enter ничего не выполнил.
/// </summary>
public partial class ConfirmDialog : DialogWindowBase
{
    /// <summary>Минимальная длина причины после обрезки пробелов.</summary>
    public const int MinReasonLength = 3;

    private readonly bool _withReason;

    public ConfirmDialog(ConfirmRequest request, bool withReason)
    {
        _withReason = withReason;
        InitializeComponent();

        HeadingText.Text = request.Title;
        MessageText.Text = request.Message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(request.Message) ? Visibility.Collapsed : Visibility.Visible;

        IconText.Text = IconMap.Get(request.IsDangerous ? "alert-triangle" : "info-circle");
        IconText.SetResourceReference(TextBlock.ForegroundProperty,
            request.IsDangerous ? "SystemFillColorCautionBrush" : "AccentTextFillColorPrimaryBrush");

        if (request.Targets.Count > 0)
        {
            TargetsLabel.Text = request.Targets.Count == 1 ? "Цель" : $"Цели ({request.Targets.Count})";
            TargetsList.ItemsSource = request.Targets;
        }
        else
        {
            TargetsPanel.Visibility = Visibility.Collapsed;
        }

        ConfirmButton.Content = request.IsDangerous ? "Выполнить" : "Продолжить";
        if (request.IsDangerous) ConfirmButton.Style = (Style)FindResource("DangerButtonStyle");

        if (withReason)
        {
            ReasonPanel.Visibility = Visibility.Visible;
            ConfirmButton.IsEnabled = false;
            FocusOnLoaded(ReasonBox);
        }
        else
        {
            FocusOnLoaded(request.IsDangerous ? CancelButton : ConfirmButton);
        }
    }

    /// <summary>Пользователь подтвердил.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Причина без лишних пробелов; null, если поле не использовалось.</summary>
    public string? Reason { get; private set; }

    /// <summary>Годится ли введённая причина.</summary>
    public static bool IsReasonValid(string? reason) => reason is not null && reason.Trim().Length >= MinReasonLength;

    private void OnReasonChanged(object sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = !_withReason || IsReasonValid(ReasonBox.Text);

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_withReason)
        {
            if (!IsReasonValid(ReasonBox.Text)) return;
            Reason = ReasonBox.Text.Trim();
        }

        Confirmed = true;
        DialogResult = true;
    }
}
