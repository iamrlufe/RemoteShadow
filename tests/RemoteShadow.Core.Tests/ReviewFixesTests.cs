using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Core.Tests;

/// <summary>Регрессионные тесты исправленных ошибок в Core.</summary>
public class ReviewFixesTests
{
    [Theory]
    [InlineData("pc1\n")]
    [InlineData("10.0.0.5\n")]
    [InlineData("pc1.corp.local\n")]
    [InlineData(" 10.0.0.5")]
    [InlineData("pc1\t")]
    public void HostNameValidator_rejects_trailing_newline_and_whitespace(string host) =>
        Assert.False(HostNameValidator.IsValid(host));

    [Fact]
    public void Dn_EscapeValue_writes_control_chars_as_hexpair()
    {
        Assert.Equal(@"a\00b", Dn.EscapeValue("a\0b"));
        Assert.Equal(@"line1\0Aline2\0D", Dn.EscapeValue("line1\nline2\r"));
    }

    [Fact]
    public void Dn_EscapeValue_roundtrips_through_GetName()
    {
        const string value = "Иванов, И.\n#1";
        Assert.Equal(value, Dn.GetName("CN=" + Dn.EscapeValue(value) + ",DC=corp"));
    }

    [Fact]
    public void GeneralizedTime_treats_unspecified_kind_as_utc()
    {
        var unspecified = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal("20260115103000.0Z", LdapFilter.GeneralizedTime(unspecified));

        var utc = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        Assert.Equal("20260115103000.0Z", LdapFilter.GeneralizedTime(utc));
        Assert.Equal("20260115103000.0Z", LdapFilter.GeneralizedTime(utc.ToLocalTime()));
    }

    [Theory]
    [InlineData("10.0.0.0/+24")]
    [InlineData("10.0.0.0/ 24")]
    [InlineData("10.0.0.0/24 /")]
    [InlineData("10.0.0.0/2 4")]
    public void Cidr_rejects_non_digit_prefix(string cidr) =>
        Assert.False(Cidr.TryParse(cidr, out _, out _));

    [Fact]
    public void Records_with_passwords_do_not_print_them()
    {
        const string secret = "S3cr3t!Pass";
        var request = new LogonRequest(LogonMode.Explicit, "corp.local", null, "ivanov", secret);
        var stored = new StoredLogon("corp.local", "dc01", "ivanov", secret);
        var choice = new PasswordChoice(secret, true);

        Assert.DoesNotContain(secret, request.ToString());
        Assert.Contains("ivanov", request.ToString());
        Assert.DoesNotContain(secret, stored.ToString());
        Assert.Contains("dc01", stored.ToString());
        Assert.DoesNotContain(secret, choice.ToString());

        // Равенство записей не меняется.
        Assert.Equal(request, new LogonRequest(LogonMode.Explicit, "corp.local", null, "ivanov", secret));
    }
}
