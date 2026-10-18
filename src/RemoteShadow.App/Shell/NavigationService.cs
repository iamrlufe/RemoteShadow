using System.Windows;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Навигация между страницами. Представление страницы создаётся через <see cref="PageRegistration.CreateView"/> один
/// раз и кэшируется; при каждом переходе ViewModel страницы (если она реализует <see cref="INavigationAware"/>)
/// получает параметр. Вызывать можно из любого потока.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _services;
    private readonly INotificationService _notifications;
    private readonly Dictionary<string, PageRegistration> _pages;
    private readonly Dictionary<string, FrameworkElement> _views = new(StringComparer.OrdinalIgnoreCase);

    public NavigationService(IServiceProvider services, IEnumerable<PageRegistration> pages, INotificationService notifications)
    {
        _services = services;
        _notifications = notifications;

        // Если два модуля зарегистрировали один Id, побеждает последняя регистрация.
        _pages = new Dictionary<string, PageRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages) _pages[page.Page.Id] = page;

        Pages = _pages.Values.OrderBy(p => p.Page.Order).ThenBy(p => p.Page.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Зарегистрированные страницы по возрастанию <c>Order</c>.</summary>
    public IReadOnlyList<PageRegistration> Pages { get; }

    public string? CurrentPageId { get; private set; }

    /// <summary>Представление текущей страницы; null, пока не выбрана ни одна.</summary>
    public FrameworkElement? CurrentView { get; private set; }

    public event EventHandler<NavigationEventArgs>? Navigated;

    public void NavigateTo(string pageId, object? parameter = null)
    {
        var dispatcher = UiThread.Dispatcher;
        if (dispatcher is null) return;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => NavigateTo(pageId, parameter));
            return;
        }

        if (!_pages.TryGetValue(pageId, out var registration))
        {
            _notifications.Error($"Раздел «{pageId}» недоступен.");
            return;
        }

        FrameworkElement view;
        try
        {
            view = GetOrCreateView(registration);
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Создание страницы «{registration.Page.Id}»", ex);
            _notifications.Error($"Не удалось открыть раздел «{registration.Page.Title}»: {ex.Message}");
            return;
        }

        CurrentPageId = registration.Page.Id;
        CurrentView = view;
        Navigated?.Invoke(this, new NavigationEventArgs(registration.Page.Id, parameter));

        // Параметр передаётся после показа: представление уже стоит в окне, и страница может рассчитывать на размеры.
        if (view.DataContext is INavigationAware aware)
        {
            try
            {
                aware.OnNavigatedTo(parameter);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"Переход на страницу «{registration.Page.Id}»", ex);
                _notifications.Error($"Ошибка в разделе «{registration.Page.Title}»: {ex.Message}");
            }
        }
    }

    private FrameworkElement GetOrCreateView(PageRegistration registration)
    {
        if (_views.TryGetValue(registration.Page.Id, out var cached)) return cached;

        var view = registration.CreateView(_services);
        _views[registration.Page.Id] = view;
        return view;
    }
}
