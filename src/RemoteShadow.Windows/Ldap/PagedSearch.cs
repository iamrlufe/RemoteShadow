using System.DirectoryServices.Protocols;

namespace RemoteShadow.Windows;

/// <summary>
/// Постраничный поиск (PageResultRequestControl): страницы читаются по одной, по cookie. Объект хранит состояние,
/// поэтому все вызовы <see cref="ReadNext"/> выполняются последовательно. Блокирующий: вызывать внутри Task.Run.
/// </summary>
internal sealed class PagedSearch
{
    private readonly SearchRequest _request;
    private readonly PageResultRequestControl _page;

    public PagedSearch(SearchRequest request, int pageSize)
    {
        _request = request;
        _page = new PageResultRequestControl(pageSize);
        _request.Controls.Add(_page);
    }

    /// <summary>true, когда сервер больше не отдаёт страниц.</summary>
    public bool Finished { get; private set; }

    /// <summary>Следующая страница; после последней <see cref="Finished"/> становится true.</summary>
    public IReadOnlyList<SearchResultEntry> ReadNext(LdapConnection ldap)
    {
        if (Finished) return Array.Empty<SearchResultEntry>();

        var response = (SearchResponse)ldap.SendRequest(_request);
        var entries = new List<SearchResultEntry>(response.Entries.Count);
        foreach (SearchResultEntry entry in response.Entries) entries.Add(entry);

        var control = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
        if (control is { Cookie: { Length: > 0 } cookie })
            _page.Cookie = cookie;
        else
            Finished = true;

        return entries;
    }

    /// <summary>Читает записи до <paramref name="max"/> штук (лишние отбрасываются). Размер страницы — не больше max.</summary>
    public static List<SearchResultEntry> ReadUpTo(LdapConnection ldap, SearchRequest request, int max)
    {
        var search = new PagedSearch(request, Math.Clamp(max, 1, 1000));
        var result = new List<SearchResultEntry>();
        while (!search.Finished && result.Count < max)
            result.AddRange(search.ReadNext(ldap));

        if (result.Count > max) result.RemoveRange(max, result.Count - max);
        return result;
    }
}
