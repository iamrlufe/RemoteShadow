using System.Windows;

namespace RemoteShadow.App.Shell.Dialogs;

/// <summary>
/// Основа модальных диалогов оболочки: фиксированная ширина, высота по содержимому, без кнопки в панели задач.
/// Enter и Esc обеспечивают кнопки с <c>IsDefault</c> и <c>IsCancel</c>.
/// </summary>
public abstract class DialogWindowBase : Window
{
    protected DialogWindowBase()
    {
        Title = "RemoteShadow";
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Окна-наследники не получают стиль Window из темы Fluent сами: фон и цвет текста задаём явно.
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    /// <summary>Ставит фокус на элемент после показа окна (раньше фокус не принимается).</summary>
    protected void FocusOnLoaded(UIElement element)
    {
        void Handler(object sender, RoutedEventArgs e)
        {
            Loaded -= Handler;
            element.Focus();
        }

        Loaded += Handler;
    }
}
