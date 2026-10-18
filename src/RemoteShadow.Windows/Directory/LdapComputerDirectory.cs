using System.DirectoryServices.Protocols;
using System.Runtime.CompilerServices;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows;

/// <summary>
/// Компьютеры и подразделения AD через LDAP. Компьютеры читаются одним постраничным запросом по всему домену
/// и отдаются по мере прихода страниц. На каждую операцию открывается своё подключение: так нет общего состояния
/// и устаревших соединений.
/// </summary>
public sealed class LdapComputerDirectory : IComputerDirectory
{
    private const int PageSize = 500;

    private readonly ICurrentConnection _current;
    private readonly object _rootGate = new();
    private IDomainConnection? _rootConnection;
    private string? _rootDn;

    public LdapComputerDirectory(ICurrentConnection current)
    {
        _current = current;
    }

    public string? DomainController => _current.Current?.DomainController;

    public async Task<string> GetRootDnAsync(CancellationToken ct)
    {
        var connection = _current.Require();
        lock (_rootGate)
        {
            if (ReferenceEquals(_rootConnection, connection) && _rootDn is not null) return _rootDn;
        }

        var root = await RunAsync(
            connection,
            new DirectoryOperationInfo(DirectoryOperation.Read),
            LdapRootDse.ReadDefaultNamingContext,
            ct).ConfigureAwait(false);

        lock (_rootGate)
        {
            _rootConnection = connection;
            _rootDn = root;
        }

        return root;
    }

    public async IAsyncEnumerable<ComputerInfo> EnumerateAsync(
        ComputerQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var connection = _current.Require();
        var rootDn = await GetRootDnAsync(ct).ConfigureAwait(false);
        var info = new DirectoryOperationInfo(DirectoryOperation.Read);

        using var session = await OpenAsync(connection, info, ct).ConfigureAwait(false);

        var request = new SearchRequest(rootDn, LdapFilters.Computers(query), SearchScope.Subtree, DirectoryMappers.ComputerAttributes);
        var search = new PagedSearch(request, PageSize);

        while (!search.Finished)
        {
            var page = await ReadPageAsync(session, search, info, ct).ConfigureAwait(false);
            foreach (var entry in page)
            {
                var computer = DirectoryMappers.MapComputer(entry.DistinguishedName, new SearchEntryAttributes(entry));
                if (computer is not null) yield return computer;
            }
        }
    }

    public async Task<IReadOnlyList<OuNode>> GetOuChildrenAsync(string parentDn, CancellationToken ct)
    {
        var connection = _current.Require();
        var rootDn = await GetRootDnAsync(ct).ConfigureAwait(false);
        var baseDn = string.IsNullOrWhiteSpace(parentDn) ? rootDn : parentDn;
        var info = new DirectoryOperationInfo(DirectoryOperation.Read, baseDn);

        using var session = await OpenAsync(connection, info, ct).ConfigureAwait(false);

        var children = await RunAsync(session, info, ldap => ReadChildren(ldap, rootDn, baseDn), ct).ConfigureAwait(false);
        if (children.Count == 0) return children;

        // Какие из найденных узлов сами содержат подразделения: один запрос по поддереву вместо запроса на каждый узел.
        // Если он не удался, стрелку раскрытия показываем у всех: лишний пустой раскрывающийся узел лучше, чем скрытая ветка.
        HashSet<string>? parentsWithChildren = null;
        try
        {
            parentsWithChildren = await RunAsync(session, info, ldap => ReadParentsOfDescendants(ldap, baseDn), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // оставляем HasChildren = true
        }

        if (parentsWithChildren is null) return children;

        return children
            .Select(node => node with { HasChildren = parentsWithChildren.Contains(Dn.Normalize(node.DistinguishedName)) })
            .ToList();
    }

    private static List<OuNode> ReadChildren(LdapConnection ldap, string rootDn, string baseDn)
    {
        var request = new SearchRequest(
            baseDn,
            LdapFilters.OuAndContainers,
            SearchScope.OneLevel,
            "name",
            "distinguishedName",
            "objectClass",
            "showInAdvancedViewOnly");

        var search = new PagedSearch(request, PageSize);
        var nodes = new List<OuNode>();
        while (!search.Finished)
        {
            foreach (var entry in search.ReadNext(ldap))
            {
                var attributes = new SearchEntryAttributes(entry);
                var dn = entry.DistinguishedName;
                var name = attributes.GetString("name") is { Length: > 0 } n ? n : Dn.GetName(dn);
                var isOu = attributes.GetStrings("objectClass").Contains("organizationalUnit", StringComparer.OrdinalIgnoreCase);

                if (OuNodeFilter.IsHidden(rootDn, baseDn, name, isOu, attributes.GetString("showInAdvancedViewOnly"))) continue;
                nodes.Add(new OuNode(dn, name));
            }
        }

        nodes.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return nodes;
    }

    /// <summary>Нормализованные DN родителей всех видимых подразделений и контейнеров под <paramref name="baseDn"/>.</summary>
    private static HashSet<string> ReadParentsOfDescendants(LdapConnection ldap, string baseDn)
    {
        var request = new SearchRequest(baseDn, LdapFilters.VisibleOuAndContainers, SearchScope.Subtree, "distinguishedName");
        var search = new PagedSearch(request, PageSize);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        while (!search.Finished)
        {
            foreach (var entry in search.ReadNext(ldap))
                parents.Add(Dn.Normalize(Dn.Parent(entry.DistinguishedName)));
        }

        return parents;
    }

    private static async Task<IReadOnlyList<SearchResultEntry>> ReadPageAsync(
        LdapSession session,
        PagedSearch search,
        DirectoryOperationInfo info,
        CancellationToken ct)
    {
        try
        {
            return await session.RunAsync(ldap => search.ReadNext(ldap), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw LdapDirectoryErrors.Translate(ex, info);
        }
    }

    private static async Task<LdapSession> OpenAsync(IDomainConnection connection, DirectoryOperationInfo info, CancellationToken ct)
    {
        try
        {
            return await LdapSession.OpenAsync(connection, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw LdapDirectoryErrors.Translate(ex, info);
        }
    }

    private static async Task<T> RunAsync<T>(LdapSession session, DirectoryOperationInfo info, Func<LdapConnection, T> work, CancellationToken ct)
    {
        try
        {
            return await session.RunAsync(work, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw LdapDirectoryErrors.Translate(ex, info);
        }
    }

    private static async Task<T> RunAsync<T>(
        IDomainConnection connection,
        DirectoryOperationInfo info,
        Func<LdapConnection, T> work,
        CancellationToken ct)
    {
        using var session = await OpenAsync(connection, info, ct).ConfigureAwait(false);
        return await RunAsync(session, info, work, ct).ConfigureAwait(false);
    }
}
