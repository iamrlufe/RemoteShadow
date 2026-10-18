using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Shell.Dialogs;

/// <summary>
/// Выбор группы: поиск с задержкой 250 мс, новый ввод отменяет предыдущий запрос. Выбор — двойной щелчок или Enter.
/// </summary>
public partial class PickGroupDialog : DialogWindowBase
{
    /// <summary>Задержка между последним нажатием клавиши и запросом.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>> _search;
    private CancellationTokenSource? _cts;

    public PickGroupDialog(
        string title,
        Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>> search)
    {
        _search = search;
        InitializeComponent();

        HeadingText.Text = title;
        System.Windows.Automation.AutomationProperties.SetName(this, title);
        ShowStatus("Введите часть названия группы.");
        FocusOnLoaded(SearchBox);
    }

    /// <summary>Выбранная группа; null, если пользователь отменил.</summary>
    public GroupInfo? Result { get; private set; }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        CancelSearch();

        var text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            LoadingBar.Visibility = Visibility.Hidden;
            ResultsList.ItemsSource = null;
            ShowStatus("Введите часть названия группы.");
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        LoadingBar.Visibility = Visibility.Visible;
        _ = SearchAsync(text, cts);
    }

    private async Task SearchAsync(string text, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Delay(SearchDelay, ct);

            // Вызов уходит из потока интерфейса: служба каталога может блокироваться до первого await.
            var found = await Task.Run(() => _search(text, ct), ct);
            ct.ThrowIfCancellationRequested();

            ResultsList.ItemsSource = found;
            if (found.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
                StatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowStatus("Ничего не найдено.");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;

            ResultsList.ItemsSource = null;
            ShowStatus(ex is OperationFailedException ? ex.Message : $"Не удалось выполнить поиск: {ex.Message}");
        }

        if (ReferenceEquals(_cts, cts)) LoadingBar.Visibility = Visibility.Hidden;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void CancelSearch()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;

        cts.Cancel();
        cts.Dispose();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectButton.IsEnabled = ResultsList.SelectedItem is GroupInfo;

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up) || ResultsList.Items.Count == 0) return;

        var index = ResultsList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
        ResultsList.SelectedIndex = Math.Clamp(index, 0, ResultsList.Items.Count - 1);
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        e.Handled = true;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Двойной щелчок по пустому месту списка или по полосе прокрутки ничего не выбирает.
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ResultsList, source) is null) return;

        Accept();
    }

    private void OnSelectClick(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ResultsList.SelectedItem is not GroupInfo group) return;

        Result = group;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelSearch();
        base.OnClosed(e);
    }
}
