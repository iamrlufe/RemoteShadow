using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace RemoteShadow.App.Shell.Login;

/// <summary>Окно входа в домен. Пароль живёт только в <c>PasswordBox</c>: модель читает его в момент входа.</summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginWindow(LoginViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PasswordProvider = () => PasswordField.Password;
        viewModel.PasswordClearer = () => PasswordField.Clear();
        viewModel.CloseRequested = ok => DialogResult = ok;
        viewModel.FocusDomainControllerRequested += (_, _) =>
        {
            ControllerBox.Focus();
            ControllerBox.SelectAll();
        };

        var storedPassword = viewModel.LoadStored();
        if (storedPassword is not null) PasswordField.Password = storedPassword;

        // Esc отменяет вход или закрывает окно. Кнопка IsCancel закрыла бы окно, не отменив идущий вход.
        InputBindings.Add(new KeyBinding(viewModel.CancelCommand, Key.Escape, ModifierKeys.None));

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsCurrentUserMode) LoginButton.Focus();
        else if (string.IsNullOrWhiteSpace(_viewModel.UserName)) UserNameBox.Focus();
        else PasswordField.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            // Закрытие окна во время входа отменяет вход, а не бросает его на полпути.
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        PasswordField.Clear();
        _viewModel.PasswordProvider = null;
        _viewModel.PasswordClearer = null;
        _viewModel.CloseRequested = null;
        base.OnClosed(e);
    }
}
