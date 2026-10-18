using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.App.Pages;
using RemoteShadow.App.Shell;

namespace RemoteShadow.App.Tests;

/// <summary>
/// Регистрация страниц: раньше <c>AddRemoteShadowPages()</c> была пустой заглушкой, и окно показывало
/// «Нет доступных разделов». Эти тесты фиксируют, что модули Connection/Sessions/Power/Users/Diagnostics
/// теперь подключены к навигации тремя страницами.
/// </summary>
public sealed class PagesRegistrationTests
{
    [Fact]
    public void AddRemoteShadowPages_RegistersThreePages()
    {
        var services = new ServiceCollection();
        services.AddRemoteShadowPages();
        using var provider = services.BuildServiceProvider();

        var pages = provider.GetServices<PageRegistration>().ToList();

        Assert.Equal(3, pages.Count);
        Assert.Contains(pages, p => p.Page.Id == PageIds.Computers);
        Assert.Contains(pages, p => p.Page.Id == PageIds.Users);
        Assert.Contains(pages, p => p.Page.Id == PageIds.Diagnostics);
    }

    [Fact]
    public void AddRemoteShadowPages_PagesHaveTitlesAndDistinctOrder()
    {
        var services = new ServiceCollection();
        services.AddRemoteShadowPages();
        using var provider = services.BuildServiceProvider();

        var pages = provider.GetServices<PageRegistration>().Select(p => p.Page).ToList();

        Assert.All(pages, p => Assert.False(string.IsNullOrWhiteSpace(p.Title)));
        Assert.All(pages, p => Assert.False(string.IsNullOrWhiteSpace(p.Icon)));
        Assert.Equal(pages.Count, pages.Select(p => p.Order).Distinct().Count());
    }

    [Fact]
    public void AddRemoteShadowPages_ComputersPageComesFirst()
    {
        var services = new ServiceCollection();
        services.AddRemoteShadowPages();
        using var provider = services.BuildServiceProvider();

        var ordered = provider.GetServices<PageRegistration>().OrderBy(p => p.Page.Order).ToList();

        Assert.Equal(PageIds.Computers, ordered[0].Page.Id);
    }
}
