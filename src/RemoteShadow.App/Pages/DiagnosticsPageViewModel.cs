using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
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
public sealed partial class DiagnosticsPageViewModel : ObservableObject, INavigationAware, IRefreshablePage, IDisposable
{
    private readonly IComputerIndex _index;
    private readonly IDiagnosticsService _diagnostics;
    private readonly INotificationService _notifications;
    private readonly ActionPresenter<DiagnosticFixTarget> _presenter;
    private CancellationTokenSource? _runCts;
    private Guid? _trackedComputerId;
    private bool _rebuildingList;
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

    /// <summary>Итоговый статус отчёта для цвета и значка вывода: проблема, предупреждение, иначе «всё в порядке».</summary>
    public DiagnosticStatus? VerdictStatus => Report is null
        ? null
        : Report.Items.Any(i => i.Status == DiagnosticStatus.Fail) ? DiagnosticStatus.Fail
        : Report.Items.Any(i => i.Status == DiagnosticStatus.Warning) ? DiagnosticStatus.Warning
        : Report.Items.Any(i => i.Status == DiagnosticStatus.Unknown) ? DiagnosticStatus.Unknown
        : DiagnosticStatus.Ok;

    /// <summary>Главный вывод крупным текстом: <see cref="DiagnosticReport.Verdict"/>, а если его нет — общий итог по проверкам.</summary>
    public string? VerdictText
    {
        get
        {
            if (Report is null) return null;
            if (!string.IsNullOrWhiteSpace(Report.Verdict)) return Report.Verdict;
            return VerdictStatus switch
            {
                DiagnosticStatus.Fail => "Найдены проблемы",
                DiagnosticStatus.Warning => "Есть замечания",
                DiagnosticStatus.Unknown => "Часть проверок выполнить не удалось",
                _ => "Проблем не найдено",
            };
        }
    }

    partial void OnReportChanged(DiagnosticReport? value)
    {
        OnPropertyChanged(nameof(VerdictStatus));
        OnPropertyChanged(nameof(VerdictText));
        CopyReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Отчёт текстом для письма или заявки. В нём только то, что вернула диагностика (имя, проверки, подсказки): секретов нет.</summary>
    public string BuildReportText()
    {
        if (Report is null || SelectedComputer is null) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"Диагностика компьютера {SelectedComputer.Name} ({Report.Host})");
        sb.AppendLine($"Проверено: {Report.CreatedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"Вывод: {VerdictText}");
        sb.AppendLine();
        foreach (var item in Report.Items)
        {
            sb.AppendLine($"[{StatusLabel(item.Status)}] {item.Title}: {item.Detail}");
            if (!string.IsNullOrWhiteSpace(item.Hint)) sb.AppendLine($"    Что исправить: {item.Hint}");
        }

        return sb.ToString();
    }

    internal static string StatusLabel(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Ok => "ОК",
        DiagnosticStatus.Warning => "Внимание",
        DiagnosticStatus.Fail => "Проблема",
        _ => "Не проверено",
    };

    [RelayCommand(CanExecute = nameof(CanCopyReport))]
    private void CopyReport()
    {
        try
        {
            Clipboard.SetDataObject(BuildReportText(), true);
            _notifications.Success("Отчёт скопирован в буфер обмена");
        }
        catch (Exception ex)
        {
            // Буфер обмена может быть занят другой программой.
            _notifications.Error($"Не удалось скопировать отчёт: {ex.Message}");
        }
    }

    private bool CanCopyReport() => Report is not null && SelectedComputer is not null;

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not DiagnoseComputerRequest request) return;

        var computer = _index.Find(request.ComputerId);
        if (computer is null)
        {
            _notifications.Error("Компьютер не найден в индексе.");
            return;
        }

        // Поиск на странице мог скрыть компьютер — тогда список сбросил бы выбор.
        if (Computers.All(c => c.Id != computer.Id)) SearchText = string.Empty;

        SelectedComputer = Computers.FirstOrDefault(c => c.Id == computer.Id) ?? computer;
        _ = RunAsync();
    }

    /// <summary>F5 — проверить выбранный компьютер ещё раз.</summary>
    public void RefreshPage(bool full)
    {
        if (RunCommand.CanExecute(null)) RunCommand.Execute(null);
    }

    partial void OnSearchTextChanged(string value) => RefreshComputers();

    partial void OnSelectedComputerChanged(ComputerInfo? value)
    {
        // Список перестраивается (событие индекса, поиск): ListBox временно сбрасывает выбор — это не смена компьютера.
        if (_rebuildingList) return;

        var id = value?.Id;
        if (id == _trackedComputerId) return; // та же запись (обновилось «в сети» и т. п.) — отчёт не сбрасываем.

        // Проверка прежнего компьютера ещё идёт: её результат не должен появиться под именем нового.
        _runCts?.Cancel();
        IsRunning = false;

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

        var selectedId = _trackedComputerId;
        _rebuildingList = true;
        try
        {
            Computers.Clear();
            foreach (var computer in source.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Computers.Add(computer);
        }
        finally
        {
            _rebuildingList = false;
        }

        // Тот же компьютер после обновления индекса остаётся выбранным, и его отчёт не сбрасывается.
        var reselected = selectedId is { } id ? Computers.FirstOrDefault(c => c.Id == id) : null;
        if (!Equals(SelectedComputer, reselected)) SelectedComputer = reselected;
        else if (reselected is null && _trackedComputerId is not null) OnSelectedComputerChanged(null);
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
