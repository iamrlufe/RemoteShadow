using System.Windows.Controls;
using System.Windows.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;

namespace RemoteShadow.App.Pages;

public partial class UsersPage : UserControl, IFilterablePage
{
    public UsersPage()
    {
        InitializeComponent();

        // Правая кнопка по пользователю: «Где работает?», действия модуля Users и копирование.
        ActionMenuBuilder.Attach(UserList, () => ViewModel?.BuildUserMenu());
    }

    private UsersPageViewModel? ViewModel => DataContext as UsersPageViewModel;

    public void FocusFilter()
    {
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        FilterBox.SelectAll();
    }

    /// <summary>Esc очищает поиск, стрелка вниз переводит фокус в результаты.</summary>
    private void OnFilterBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when FilterBox.Text.Length > 0:
                FilterBox.Clear();
                e.Handled = true;
                break;

            case Key.Down when UserList.Items.Count > 0:
                ListFocus.FocusSelectedOrFirst(UserList);
                e.Handled = true;
                break;
        }
    }

    private void OnUserListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key == Key.Enter && vm.SelectedUser is not null)
        {
            vm.FindWhereLoggedOnCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.CopyLoginCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnUserListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !ListFocus.IsOnItem(e.OriginalSource)) return;

        ViewModel?.FindWhereLoggedOnCommand.Execute(null);
        e.Handled = true;
    }
}
