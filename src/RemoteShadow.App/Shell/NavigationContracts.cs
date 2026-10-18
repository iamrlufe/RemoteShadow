using RemoteShadow.Core.Actions;

namespace RemoteShadow.App.Shell;

/// <summary>Страница навигации: метаданные из Core и фабрика представления. Регистрируется в DI singleton'ом.</summary>
/// <param name="CreateView">Создаёт UserControl страницы и ставит ему DataContext (ViewModel берётся из провайдера).</param>
public sealed record PageRegistration(IPage Page, Func<IServiceProvider, System.Windows.FrameworkElement> CreateView);

/// <summary>Куда и с чем перейти. Параметры — записи ниже.</summary>
public interface INavigationService
{
    /// <summary>Id текущей страницы.</summary>
    string? CurrentPageId { get; }

    /// <summary>Переходит на страницу. Если ViewModel страницы реализует <see cref="INavigationAware"/>, ей придёт параметр.</summary>
    void NavigateTo(string pageId, object? parameter = null);

    event EventHandler<NavigationEventArgs>? Navigated;
}

public sealed class NavigationEventArgs(string pageId, object? parameter) : EventArgs
{
    public string PageId { get; } = pageId;
    public object? Parameter { get; } = parameter;
}

/// <summary>ViewModel страницы получает параметр перехода (и при первом показе, и при повторном).</summary>
public interface INavigationAware
{
    void OnNavigatedTo(object? parameter);
}

/// <summary>Id встроенных страниц.</summary>
public static class PageIds
{
    public const string Computers = "computers";
    public const string Users = "users";
    public const string Favorites = "favorites";
    public const string Journal = "journal";
    public const string Diagnostics = "diagnostics";
    public const string Settings = "settings";
}

/// <summary>Параметры перехода.</summary>
public sealed record SelectComputerRequest(Guid ComputerId, int? SessionId = null);

public sealed record SelectUserRequest(string SamAccountName);

/// <summary>Открыть «Компьютеры» и запустить «Найти пользователя» по тексту.</summary>
public sealed record FindUserRequest(string Text);

/// <summary>Открыть «Диагностика» для компьютера.</summary>
public sealed record DiagnoseComputerRequest(Guid ComputerId);
