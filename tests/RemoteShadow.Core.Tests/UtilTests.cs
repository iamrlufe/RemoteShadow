using RemoteShadow.Core;
using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Core.Tests;

public class DnTests
{
    [Theory]
    [InlineData("CN=PC1,OU=Склад,DC=corp,DC=local", "OU=Склад,DC=corp,DC=local")]
    [InlineData(@"CN=A\, B,OU=X,DC=corp", "OU=X,DC=corp")]
    [InlineData("DC=corp", "")]
    [InlineData("", "")]
    public void Parent_removes_first_rdn(string dn, string expected) =>
        Assert.Equal(expected, Dn.Parent(dn));

    [Theory]
    [InlineData("OU=Бухгалтерия,DC=corp", "Бухгалтерия")]
    [InlineData(@"OU=Ivanov\, A,DC=x", "Ivanov, A")]
    [InlineData(@"CN=\D0\9F\D0\B5\D1\82\D1\80\D0\BE\D0\B2,DC=x", "Петров")]
    public void GetName_unescapes(string dn, string expected) =>
        Assert.Equal(expected, Dn.GetName(dn));

    [Fact]
    public void Descendant_matches_by_suffix_ignoring_case_and_spaces()
    {
        Assert.True(Dn.IsDescendantOf("CN=PC1,OU=Склад,OU=Офис,DC=corp,DC=local", "ou=офис, dc=corp,dc=local"));
        Assert.True(Dn.IsDescendantOf("CN=PC1,OU=Офис,DC=corp,DC=local", "OU=Офис,DC=corp,DC=local"));
        Assert.False(Dn.IsDescendantOf("OU=Офис,DC=corp,DC=local", "OU=Офис,DC=corp,DC=local"));
        Assert.False(Dn.IsDescendantOf("CN=PC1,OU=Офис2,DC=corp,DC=local", "OU=Офис,DC=corp,DC=local"));
    }

    [Fact]
    public void DirectChild_is_exact_parent_only()
    {
        Assert.True(Dn.IsDirectChildOf("CN=PC1,OU=Склад,DC=corp", "OU=Склад,DC=corp"));
        Assert.False(Dn.IsDirectChildOf("CN=PC1,OU=Склад,OU=Офис,DC=corp", "OU=Офис,DC=corp"));
    }

    [Fact]
    public void Dns_domain_round_trip()
    {
        Assert.Equal("DC=corp,DC=local", Dn.FromDnsDomain("corp.local"));
        Assert.Equal("corp.local", Dn.ToDnsDomain("OU=A,DC=corp,DC=local"));
    }

    [Fact]
    public void EscapeValue_escapes_specials() =>
        Assert.Equal(@"Smith\, John\+1", Dn.EscapeValue("Smith, John+1"));

    [Fact]
    public void ComputerInfo_parent_dn_uses_dn()
    {
        var c = new ComputerInfo { Id = Guid.NewGuid(), Name = "PC1", DistinguishedName = "CN=PC1,OU=Склад,DC=corp" };
        Assert.Equal("OU=Склад,DC=corp", c.ParentDn);
        Assert.Equal("PC1", c.HostName);
        Assert.Equal("pc1.corp.local", (c with { DnsName = "pc1.corp.local" }).HostName);
    }
}

public class LdapFilterTests
{
    [Fact]
    public void Escape_neutralizes_filter_metacharacters() =>
        Assert.Equal(@"a\2ab\28c\29\5cd", LdapFilter.Escape(@"a*b(c)\d"));

    [Fact]
    public void GeneralizedTime_is_utc_formatted() =>
        Assert.Equal("20260115103000.0Z", LdapFilter.GeneralizedTime(new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc)));
}

public class HostNameValidatorTests
{
    [Theory]
    [InlineData("PC-ACC-014")]
    [InlineData("pc-acc-014.corp.local")]
    [InlineData("srv_01")]
    [InlineData("10.0.0.5")]
    public void Accepts_normal_hosts(string host) => Assert.True(HostNameValidator.IsValid(host));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-evil")]
    [InlineData("/v:evil")]
    [InlineData("pc1 /admin")]
    [InlineData("pc1;calc")]
    [InlineData("pc1&calc")]
    [InlineData("\"pc1\"")]
    [InlineData("pc1\r\n")]
    [InlineData("fe80::1")]
    [InlineData("-")]
    public void Rejects_dangerous_or_malformed(string host) => Assert.False(HostNameValidator.IsValid(host));

    [Fact]
    public void Require_throws_with_invalid_input_failure()
    {
        var ex = Assert.Throws<OperationFailedException>(() => HostNameValidator.Require("-x"));
        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
    }
}

public class AccountNameParserTests
{
    [Fact]
    public void Parses_all_forms()
    {
        Assert.Equal(new ParsedAccountName("CORP", "ivanov", null), AccountNameParser.Parse(@"CORP\ivanov"));
        Assert.Equal(new ParsedAccountName(null, "ivanov", "corp.local"), AccountNameParser.Parse("ivanov@corp.local"));
        Assert.Equal(new ParsedAccountName(null, "ivanov", null), AccountNameParser.Parse(" ivanov "));
    }
}

public class PasswordGeneratorTests
{
    [Fact]
    public void Generated_password_has_all_character_classes()
    {
        for (var i = 0; i < 200; i++)
        {
            var p = PasswordGenerator.Generate(14);
            Assert.Equal(14, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
        }
    }

    [Fact]
    public void Too_short_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(4));
}

public class ModelTests
{
    [Theory]
    [InlineData(SessionState.Active, true)]
    [InlineData(SessionState.Disconnected, false)]
    [InlineData(SessionState.Connected, false)]
    public void Shadow_requires_active_session_with_user(SessionState state, bool expected)
    {
        var s = new SessionInfo { Id = 1, UserName = "ivanov", State = state };
        Assert.Equal(expected, s.CanShadow);
        Assert.Equal(expected, s.ShadowBlockedReason is null);
    }

    [Fact]
    public void Session_without_user_cannot_be_shadowed()
    {
        var s = new SessionInfo { Id = 0, State = SessionState.Active };
        Assert.False(s.CanShadow);
        Assert.NotNull(s.ShadowBlockedReason);
    }

    [Fact]
    public void Bulk_summary_formats_counts()
    {
        var results = new List<TargetResult>();
        for (var i = 0; i < 7; i++) results.Add(new TargetResult($"t{i}", ActionResult.Ok()));
        results.Add(new TargetResult("t7", ActionResult.Failed("нет доступа")));
        var bulk = new BulkResult(results, false);
        Assert.Equal("7 из 8 успешно", bulk.Summary);
        Assert.Equal(7, bulk.Succeeded);
        Assert.Equal(1, bulk.Failed);
        Assert.False(bulk.AllOk);
    }

    [Fact]
    public void Single_target_summary_is_its_message()
    {
        var bulk = new BulkResult(new[] { new TargetResult("t", ActionResult.Failed("нет доступа")) }, false);
        Assert.Equal("нет доступа", bulk.Summary);
    }

    [Fact]
    public void CurrentConnection_disposes_previous_and_requires_login()
    {
        var cc = new Services.CurrentConnection();
        Assert.Throws<OperationFailedException>(() => cc.Require());
    }
}
