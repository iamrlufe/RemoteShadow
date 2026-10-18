using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;
using static RemoteShadow.Infrastructure.Tests.TestData;

namespace RemoteShadow.Infrastructure.Tests;

public class ComputerIndexOuTests
{
    private const string AccountingGroupOu = "OU=Группа 1," + AccountingOu;
    private const string ComputersContainer = "CN=Computers," + Root;

    // Запятая внутри имени подразделения экранирована: это одно подразделение «Склад, Север», а не два уровня.
    private const string NorthWarehouseOu = @"OU=Склад\, Север," + OfficeOu;

    private static string[] Names(IEnumerable<ComputerInfo> computers) => computers.Select(c => c.Name).ToArray();

    private static async Task<IndexFixture> LoadedAsync()
    {
        var fx = new IndexFixture().Seed(
            Pc("OFFICE-2", OfficeOu, id: 1),
            Pc("OFFICE-1", OfficeOu, id: 2),
            Pc("ACC-B", AccountingOu, id: 3),
            Pc("ACC-A", AccountingOu, id: 4),
            Pc("GROUP-1", AccountingGroupOu, id: 5),
            Pc("STOCK-1", WarehouseOu, id: 6),
            Pc("NORTH-1", NorthWarehouseOu, id: 7),
            Pc("NEW-1", ComputersContainer, id: 8),
            Pc("OTHER-1", "OU=Серверы," + Root, id: 9),
            Pc("FAKE-OFFICE", "OU=Фиктивный Офис," + Root, id: 10));
        await fx.FullLoadAsync();
        return fx;
    }

    [Fact]
    public async Task Direct_children_only_when_nesting_is_off()
    {
        using var fx = await LoadedAsync();

        Assert.Equal(new[] { "OFFICE-1", "OFFICE-2" }, Names(fx.Index.GetByOu(OfficeOu, includeNested: false)));
        Assert.Equal(new[] { "ACC-A", "ACC-B" }, Names(fx.Index.GetByOu(AccountingOu, includeNested: false)));
        Assert.Equal(new[] { "GROUP-1" }, Names(fx.Index.GetByOu(AccountingGroupOu, includeNested: false)));
    }

    [Fact]
    public async Task All_descendants_when_nesting_is_on_sorted_by_name()
    {
        using var fx = await LoadedAsync();

        var office = fx.Index.GetByOu(OfficeOu, includeNested: true);
        var accounting = fx.Index.GetByOu(AccountingOu, includeNested: true);

        Assert.Equal(
            new[] { "ACC-A", "ACC-B", "GROUP-1", "NORTH-1", "OFFICE-1", "OFFICE-2", "STOCK-1" },
            Names(office));
        Assert.Equal(new[] { "ACC-A", "ACC-B", "GROUP-1" }, Names(accounting));
    }

    [Fact]
    public async Task Domain_root_with_nesting_gives_everything_and_without_gives_nothing_here()
    {
        using var fx = await LoadedAsync();

        Assert.Equal(10, fx.Index.GetByOu(Root, includeNested: true).Count);
        Assert.Empty(fx.Index.GetByOu(Root, includeNested: false));
    }

    [Fact]
    public async Task Container_is_handled_like_an_ou()
    {
        using var fx = await LoadedAsync();

        Assert.Equal(new[] { "NEW-1" }, Names(fx.Index.GetByOu(ComputersContainer, includeNested: false)));
        Assert.Equal(new[] { "NEW-1" }, Names(fx.Index.GetByOu("cn=computers,dc=corp,dc=local", includeNested: true)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dn_comparison_ignores_case_and_spaces_after_separators(bool nested)
    {
        using var fx = await LoadedAsync();

        var variant = "ou=бухгалтерия, OU=ОФИС ,dc=corp , DC=Local";

        Assert.Equal(
            Names(fx.Index.GetByOu(AccountingOu, nested)),
            Names(fx.Index.GetByOu(variant, nested)));
        Assert.NotEmpty(fx.Index.GetByOu(variant, nested));
    }

    [Fact]
    public async Task Escaped_comma_is_not_an_ou_separator()
    {
        using var fx = await LoadedAsync();

        var exact = fx.Index.GetByOu(NorthWarehouseOu, includeNested: false);
        var sameInOtherCase = fx.Index.GetByOu(@"ou=СКЛАД\, СЕВЕР,ou=офис,dc=corp,dc=local", includeNested: false);

        Assert.Equal(new[] { "NORTH-1" }, Names(exact));
        Assert.Equal(new[] { "NORTH-1" }, Names(sameInOtherCase));
    }

    [Fact]
    public async Task Ou_named_like_a_fragment_of_an_escaped_name_does_not_match()
    {
        using var fx = await LoadedAsync();

        // «OU=Склад» и «OU=Север» — не то же самое, что «OU=Склад\, Север».
        Assert.DoesNotContain("NORTH-1", Names(fx.Index.GetByOu(WarehouseOu, includeNested: true)));
        Assert.Empty(fx.Index.GetByOu("OU=Север," + OfficeOu, includeNested: true));
        Assert.Equal(new[] { "STOCK-1" }, Names(fx.Index.GetByOu(WarehouseOu, includeNested: false)));
    }

    [Fact]
    public async Task Similar_names_and_partial_suffixes_do_not_match()
    {
        using var fx = await LoadedAsync();

        // «Фиктивный Офис» не вложен в «Офис», хотя строка DN заканчивается похоже.
        Assert.DoesNotContain("FAKE-OFFICE", Names(fx.Index.GetByOu(OfficeOu, includeNested: true)));
        Assert.Equal(new[] { "FAKE-OFFICE" }, Names(fx.Index.GetByOu("OU=Фиктивный Офис," + Root, includeNested: false)));
        Assert.Empty(fx.Index.GetByOu("OU=Бухгалтерия," + Root, includeNested: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OU=Нет такого,DC=corp,DC=local")]
    public async Task Empty_or_unknown_ou_gives_empty_list(string ouDn)
    {
        using var fx = await LoadedAsync();

        Assert.Empty(fx.Index.GetByOu(ouDn, includeNested: false));
        Assert.Empty(fx.Index.GetByOu(ouDn, includeNested: true));
    }

    [Fact]
    public async Task Results_agree_with_core_dn_helpers()
    {
        using var fx = await LoadedAsync();
        var ous = new[] { Root, OfficeOu, AccountingOu, WarehouseOu, AccountingGroupOu, NorthWarehouseOu, ComputersContainer };

        foreach (var ou in ous)
        {
            var direct = fx.Index.All.Where(c => Dn.IsDirectChildOf(c.DistinguishedName, ou)).Select(c => c.Id).Order();
            var nested = fx.Index.All.Where(c => Dn.IsDescendantOf(c.DistinguishedName, ou)).Select(c => c.Id).Order();

            Assert.Equal(direct, fx.Index.GetByOu(ou, false).Select(c => c.Id).Order());
            Assert.Equal(nested, fx.Index.GetByOu(ou, true).Select(c => c.Id).Order());
        }
    }

    [Fact]
    public async Task Moved_computer_changes_list_on_next_refresh()
    {
        using var fx = await LoadedAsync();
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        var moved = fx.Ad.Computers.Single(c => c.Name == "STOCK-1") with
        {
            DistinguishedName = "CN=STOCK-1," + AccountingOu,
            WhenChanged = fx.Time.GetUtcNow().UtcDateTime,
        };
        fx.Ad.Computers[fx.Ad.Computers.FindIndex(c => c.Id == moved.Id)] = moved;

        await fx.Index.RefreshAsync(full: false, CancellationToken.None);

        Assert.Empty(fx.Index.GetByOu(WarehouseOu, includeNested: false));
        Assert.Contains("STOCK-1", Names(fx.Index.GetByOu(AccountingOu, includeNested: false)));
    }
}
