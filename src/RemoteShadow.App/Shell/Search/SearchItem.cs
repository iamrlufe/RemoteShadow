namespace RemoteShadow.App.Shell.Search;

public enum SearchItemKind
{
    /// <summary>Заголовок секции: «Где сейчас», «Компьютеры», «Пользователи».</summary>
    Header,

    /// <summary>Пояснение внутри секции («Поиск…»). Не выбирается.</summary>
    Message,

    /// <summary>Пользователь, найденный в кэше сессий: открывает компьютер и сессию.</summary>
    Location,

    Computer,
    User,

    /// <summary>Последний пункт: найти пользователя на компьютерах.</summary>
    FindUser,
}

/// <summary>Строка панели поиска Ctrl+K: заголовок секции, пояснение или выбираемый результат.</summary>
public sealed class SearchItem
{
    private SearchItem(SearchItemKind kind, string key, string title)
    {
        Kind = kind;
        Key = key;
        Title = title;
    }

    public SearchItemKind Kind { get; }

    /// <summary>Устойчивый ключ: по нему при обновлении списка сохраняется выбранная строка.</summary>
    public string Key { get; }

    public string Title { get; }
    public string? Subtitle { get; private init; }

    /// <summary>Имя значка (Tabler) для <see cref="IconMap"/>.</summary>
    public string Icon { get; private init; } = "search";

    /// <summary>Только для компьютеров: в сети (true), не в сети (false), не проверялся (null).</summary>
    public bool? Online { get; private init; }

    public Guid? ComputerId { get; private init; }
    public int? SessionId { get; private init; }
    public string? SamAccountName { get; private init; }

    /// <summary>Исходный текст запроса — для пункта «Найти пользователя…».</summary>
    public string? Query { get; private init; }

    public bool IsHeader => Kind == SearchItemKind.Header;
    public bool IsMessage => Kind == SearchItemKind.Message;
    public bool IsSelectable => Kind is not (SearchItemKind.Header or SearchItemKind.Message);
    public bool ShowsOnlineStatus => Kind == SearchItemKind.Computer;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    /// <summary>«в сети» / «не в сети» / пусто — подпись справа у компьютера.</summary>
    public string OnlineText => Online switch
    {
        true => "в сети",
        false => "не в сети",
        _ => string.Empty,
    };

    public static SearchItem CreateHeader(string title) =>
        new(SearchItemKind.Header, "header:" + title, title);

    public static SearchItem CreateMessage(string section, string text) =>
        new(SearchItemKind.Message, "message:" + section, text);

    public static SearchItem CreateLocation(Guid computerId, int sessionId, string title, string? subtitle) =>
        new(SearchItemKind.Location, $"location:{computerId:N}:{sessionId}", title)
        {
            Subtitle = subtitle,
            Icon = "user",
            ComputerId = computerId,
            SessionId = sessionId,
        };

    public static SearchItem CreateComputer(Guid computerId, string name, string? subtitle, bool? online) =>
        new(SearchItemKind.Computer, "computer:" + computerId.ToString("N"), name)
        {
            Subtitle = subtitle,
            Icon = "device-desktop",
            Online = online,
            ComputerId = computerId,
        };

    public static SearchItem CreateUser(string samAccountName, string title, string? subtitle) =>
        new(SearchItemKind.User, "user:" + samAccountName.ToLowerInvariant(), title)
        {
            Subtitle = subtitle,
            Icon = "user",
            SamAccountName = samAccountName,
        };

    public static SearchItem CreateFindUser(string query) =>
        new(SearchItemKind.FindUser, "find-user", $"Найти пользователя «{query}» на компьютерах…")
        {
            Subtitle = "Покажет, на каком компьютере он сейчас работает",
            Icon = "search",
            Query = query,
        };
}
