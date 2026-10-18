using RemoteShadow.Core.Models;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class LdapFiltersTests
{
    [Fact]
    public void Computers_Default_ExcludesDisabled()
    {
        var filter = LdapFilters.Computers(new ComputerQuery());

        Assert.Equal("(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))", filter);
    }

    [Fact]
    public void Computers_IncludeDisabled_HasNoUacClause()
    {
        var filter = LdapFilters.Computers(new ComputerQuery(IncludeDisabled: true));

        Assert.Equal("(objectCategory=computer)", filter);
    }

    [Fact]
    public void Computers_ChangedSince_AddsWhenChanged()
    {
        var since = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var filter = LdapFilters.Computers(new ComputerQuery(since));

        Assert.Equal(
            "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(whenChanged>=20260115103000.0Z))",
            filter);
    }

    [Fact]
    public void Computers_ChangedSinceWithDisabled()
    {
        var since = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var filter = LdapFilters.Computers(new ComputerQuery(since, IncludeDisabled: true));

        Assert.Equal("(&(objectCategory=computer)(whenChanged>=20260115103000.0Z))", filter);
    }

    [Fact]
    public void UserSearch_HasAllFields()
    {
        var filter = LdapFilters.UserSearch("ivanov");

        Assert.Equal(
            "(&(objectCategory=person)(objectClass=user)"
            + "(|(anr=ivanov)(sAMAccountName=ivanov*)(displayName=*ivanov*)(department=*ivanov*)(telephoneNumber=*ivanov*)))",
            filter);
    }

    [Fact]
    public void UserSearch_KeepsCyrillic()
    {
        var filter = LdapFilters.UserSearch("Иванов");

        Assert.Contains("(anr=Иванов)", filter);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("a*)(objectClass=*")]
    [InlineData("x)(|(objectClass=*)")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData("))((")]
    [InlineData(")(userAccountControl:1.2.840.113556.1.4.803:=2")]
    public void UserSearch_InjectionCharactersAreEscaped(string text)
    {
        var benign = LdapFilters.UserSearch("abc");
        var filter = LdapFilters.UserSearch(text);

        // Структура фильтра не меняется: столько же скобок и звёздочек, сколько у безопасного текста.
        Assert.Equal(CountOf(benign, '('), CountOf(filter, '('));
        Assert.Equal(CountOf(benign, ')'), CountOf(filter, ')'));
        Assert.Equal(CountOf(benign, '*'), CountOf(filter, '*'));
        Assert.DoesNotContain('\0', filter);
    }

    [Fact]
    public void UserSearch_WildcardIsEscaped()
    {
        var filter = LdapFilters.UserSearch("a*b");

        Assert.Contains("(anr=a\\2ab)", filter);
        Assert.Contains("(sAMAccountName=a\\2ab*)", filter);
        Assert.Contains("(displayName=*a\\2ab*)", filter);
    }

    [Fact]
    public void UserSearch_ParenthesesAndBackslashAreEscaped()
    {
        var filter = LdapFilters.UserSearch("a(b)c\\d");

        Assert.Contains("(anr=a\\28b\\29c\\5cd)", filter);
    }

    [Fact]
    public void UserBySam_Escapes()
    {
        Assert.Equal(
            "(&(objectCategory=person)(objectClass=user)(sAMAccountName=ivan\\2a\\29ov))",
            LdapFilters.UserBySam("ivan*)ov"));
    }

    [Fact]
    public void UserByUpn_Escapes()
    {
        Assert.Equal(
            "(&(objectCategory=person)(objectClass=user)(userPrincipalName=a\\28b@corp.local))",
            LdapFilters.UserByUpn("a(b@corp.local"));
    }

    [Fact]
    public void GroupSearch_HasCnAndSam()
    {
        Assert.Equal(
            "(&(objectCategory=group)(|(cn=*sales*)(sAMAccountName=*sales*)))",
            LdapFilters.GroupSearch("sales"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("a)(cn=*")]
    [InlineData("\\")]
    public void GroupSearch_InjectionIsEscaped(string text)
    {
        var benign = LdapFilters.GroupSearch("abc");
        var filter = LdapFilters.GroupSearch(text);

        Assert.Equal(CountOf(benign, '('), CountOf(filter, '('));
        Assert.Equal(CountOf(benign, ')'), CountOf(filter, ')'));
        Assert.Equal(CountOf(benign, '*'), CountOf(filter, '*'));
    }

    [Fact]
    public void OuFilters_AreFixed()
    {
        Assert.Equal("(|(objectClass=organizationalUnit)(objectClass=container))", LdapFilters.OuAndContainers);
        Assert.Equal(
            "(&(|(objectClass=organizationalUnit)(objectClass=container))(!(showInAdvancedViewOnly=TRUE)))",
            LdapFilters.VisibleOuAndContainers);
    }

    private static int CountOf(string text, char c) => text.Count(x => x == c);
}
