using System.Windows.Controls;
using System.Windows.Input;
using RemoteShadow.App.Shell;

namespace RemoteShadow.App.Pages;

public partial class DiagnosticsPage : UserControl, IFilterablePage
{
    public DiagnosticsPage()
    {
        InitializeComponent();
    }

    private DiagnosticsPageViewModel? ViewModel => DataContext as DiagnosticsPageViewModel;

    public void FocusFilter()
    {
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        FilterBox.SelectAll();
    }

    /// <summary>Esc очищает поиск, стрелка вниз переводит фокус в список компьютеров.</summary>
    private void OnFilterBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when FilterBox.Text.Length > 0:
                FilterBox.Clear();
                e.Handled = true;
                break;

            case Key.Down when ComputerList.Items.Count > 0:
                ListFocus.FocusSelectedOrFirst(ComputerList);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Enter по компьютеру — запустить проверку.</summary>
    private void OnComputerListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { SelectedComputer: not null } vm) return;

        if (vm.RunCommand.CanExecute(null)) vm.RunCommand.Execute(null);
        e.Handled = true;
    }

    private void OnComputerListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !ListFocus.IsOnItem(e.OriginalSource) || ViewModel is not { } vm) return;

        if (vm.RunCommand.CanExecute(null)) vm.RunCommand.Execute(null);
        e.Handled = true;
    }
}
