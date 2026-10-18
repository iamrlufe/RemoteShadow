using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace RemoteShadow.App.Shell;

public enum ToastKind
{
    Info,
    Success,
    Error,
}

/// <summary>Одно всплывающее уведомление. Создаётся и живёт в потоке интерфейса.</summary>
public sealed class ToastItem
{
    private readonly Action<ToastItem> _dismiss;
    private DispatcherTimer? _timer;

    public ToastItem(ToastKind kind, string message, Action<ToastItem> dismiss)
    {
        Kind = kind;
        Message = message;
        _dismiss = dismiss;
        DismissCommand = new RelayCommand(() => _dismiss(this));
    }

    public ToastKind Kind { get; }
    public string Message { get; }

    /// <summary>Глиф значка по типу уведомления.</summary>
    public string Glyph => IconMap.Get(Kind switch
    {
        ToastKind.Success => "circle-check",
        ToastKind.Error => "circle-x",
        _ => "info-circle",
    });

    /// <summary>Закрывает уведомление по клику.</summary>
    public ICommand DismissCommand { get; }

    /// <summary>Сколько уведомление висит: ошибки дольше, чтобы успеть прочитать.</summary>
    public static TimeSpan LifetimeOf(ToastKind kind) => kind == ToastKind.Error ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4);

    public void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer { Interval = LifetimeOf(Kind) };
        _timer.Tick += (_, _) => _dismiss(this);
        _timer.Start();
    }

    public void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }
}
