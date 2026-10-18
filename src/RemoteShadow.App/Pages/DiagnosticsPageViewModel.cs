using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteShadow.App.Shell;
using RemoteShadow.App.Shell.Actions;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Modules;

namespace RemoteShadow.App.Pages;

/// <summary>Одна строка отчёта диагностики: сама проверка и, если применимо, кнопка «Исправить» (модуль Diagnostics).</summary>
public sealed record DiagnosticItemRow(DiagnosticItem Item, ActionButtonModel? FixButton);

/// <summary>
/// Страница «Диагностика»: выбор компьютера, запуск <see cref="IDiagnosticsService.RunAsync"/> и кнопка «Исправить»
/// для проверок с <see cref="DiagnosticItem.FixId"/> (действие <see cref="ApplyDiagnosticFixAction"/> модуля Diagnostics).
/// </summary>
public sealed partial class DiagnosticsPageViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly IComputerIndex _index;
    private readonly IDiagnosticsService _diagnostics;
    private readonly INotificationService _notifications;
    private readonly ActionPresenter<DiagnosticFixTarget> _presenter;
    private CancellationTokenSource? _runCts;
    private Guid? _trackedComputerId;
    private bool _disposed;

    public DiagnosticsPageViewModel(
        IComputerIndex index, IDiagnosticsService diagnostics, IActionRunner runner, INotificationService notifications)
    {
        _index = index;
        _diagnostics = diagnostics;
        _notifications = notifications;
        _presenter = new ActionPresenter<DiagnosticFixTarget>(runner, notifications);
        _presenter.PropertyChanged += OnPresenterPropertyChanged;
        _index.Changed += OnIndexChanged;

        RefreshComputers();
    }

    public ObservableCollection<ComputerInfo> Computers { get; } = new();
    public ObservableCollection<DiagnosticItemRow> Items { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private ComputerInfo? _selectedComputer;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private DiagnosticReport? _report;

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not DiagnoseComputerRequest request) return;

        var computer = _index.Find(request.ComputerId);
        if (computer is null)
        {
            _notifications.Error("Компьютер не найден в индексе.");
            return;
        }

        SelectedComputer = computer;
        _ = RunAsync();
    }

    partial void OnSearchTextChanged(string value) => RefreshComputers();

    partial void OnSelectedComputerChanged(ComputerInfo? value)
    {
        var id = value?.Id;
        if (id == _trackedComputerId) return; // та же запись (обновилось «в сети» и т. п.) — отчёт не сбрасываем.

        _trackedComputerId = id;
        Report = null;
        Error = null;
        Items.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var computer = SelectedComputer;
        if (computer is null) return;

        _runCts?.Cancel();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        Error = null;
        try
        {
            var report = await _diagnostics.RunAsync(computer, cts.Token);
            if (cts.IsCancellationRequested) return;

            Report = report;
            RebuildItems(computer, report);
        }
        catch (OperationCanceledException)
        {
            // Выбрали другой компьютер — отменённый запуск не ошибка.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_runCts, cts)) IsRunning = false;
        }
    }

    private bool CanRun() => SelectedComputer is not null;

    private void RebuildItems(ComputerInfo computer, DiagnosticReport report)
    {
        Items.Clear();
        foreach (var item in report.Items)
        {
            // BuildButtons всегда возвращает одну (возможно выключенную) кнопку для единственной цели — показываем
            // только включённую: выключенную кнопку «Исправить» показывать незачем, у строки и так есть Hint.
            var buttons = _presenter.BuildButtons(new[] { new DiagnosticFixTarget(computer, item) }, null);
            Items.Add(new DiagnosticItemRow(item, buttons.FirstOrDefault(b => b.IsEnabled)));
        }
    }

    // «Исправить» нажато и действие завершилось: отчёт мог измениться (хотя подсказка и так просит перезапустить).
    private void OnPresenterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActionPresenter<DiagnosticFixTarget>.IsBusy) && !_presenter.IsBusy && SelectedComputer is not null)
            _ = RunAsync();
    }

    private void OnIndexChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        if (!_disposed) RefreshComputers();
    });

    private void RefreshComputers()
    {
        var text = SearchText.Trim();
        var source = text.Length == 0 ? _index.All : _index.Search(text, 200);

        var selectedId = SelectedComputer?.Id;
        Computers.Clear();
        foreach (var computer in source.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Computers.Add(computer);

        SelectedComputer = selectedId is { } id ? Computers.FirstOrDefault(c => c.Id == id) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _index.Changed -= OnIndexChanged;
        _presenter.PropertyChanged -= OnPresenterPropertyChanged;
        _runCts?.Cancel();
    }
}
