using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.App.Pages;
using RemoteShadow.App.Shell;

namespace RemoteShadow.App.Tests;

/// <summary>
/// Регистрация страниц: раньше <c>AddRemoteShadowPages()</c> была пустой заглушкой, и окно показывало
/// «Нет доступных разделов». Эти тесты фиксируют, что модули Connection/Sessions/Power/Users/Diagnostics
/// подключены к навигации страницами «Компьютеры», «Пользователи», «Избранное» и «Диагностика».
/// </summary>
public sealed class PagesRegistrationTests
{
    [Fact]
    public void AddRemoteShadowPages_RegistersFourPages()
    {
        var services = new ServiceCollection();
        services.AddRemoteShadowPages();
        using var provider = services.BuildServiceProvider();

        var pages = provider.GetServices<PageRegistration>().ToList();

        Assert.Equal(4, pages.Count);
        Assert.Contains(pages, p => p.Page.Id == PageIds.Computers);
        Assert.Contains(pages, p => p.Page.Id == PageIds.Users);
        Assert.Contains(pages, p => p.Page.Id == PageIds.Favorites);
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

    [Fact]
    public void AddRemoteShadowPages_OrderFollowsNavigationPlan()
    {
        var services = new ServiceCollection();
        services.AddRemoteShadowPages();
        using var provider = services.BuildServiceProvider();

        var orders = provider.GetServices<PageRegistration>().ToDictionary(p => p.Page.Id, p => p.Page.Order);

        Assert.Equal(10, orders[PageIds.Computers]);
        Assert.Equal(20, orders[PageIds.Users]);
        Assert.Equal(30, orders[PageIds.Favorites]);
        Assert.Equal(50, orders[PageIds.Diagnostics]);
    }
}
