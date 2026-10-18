using System.Text;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class CredentialRecordCodecTests
{
    [Fact]
    public void TargetName_IsFixed()
    {
        Assert.Equal("RemoteShadow:login", CredentialRecordCodec.TargetName);
    }

    [Theory]
    [InlineData("corp.local", "ivanov", @"corp.local\ivanov")]
    [InlineData("CORP", "adm-ivanov", @"CORP\adm-ivanov")]
    [InlineData("", "ivanov", "ivanov")]
    [InlineData(null, "ivanov", "ivanov")]
    [InlineData("", "ivanov@corp.local", "ivanov@corp.local")]
    public void BuildUserName(string? domain, string userName, string expected)
    {
        Assert.Equal(expected, CredentialRecordCodec.BuildUserName(domain, userName));
    }

    [Theory]
    [InlineData(@"corp.local\ivanov", "corp.local", "ivanov")]
    [InlineData(@"CORP\adm-ivanov", "CORP", "adm-ivanov")]
    [InlineData("ivanov", null, "ivanov")]
    [InlineData("ivanov@corp.local", null, "ivanov@corp.local")]
    [InlineData(@"corp.local\a\b", "corp.local", @"a\b")] // делим по первой «\»
    [InlineData(@"\ivanov", null, @"\ivanov")]
    [InlineData(@"corp\", null, @"corp\")]
    public void ParseUserName(string value, string? domain, string userName)
    {
        Assert.Equal((domain, userName), CredentialRecordCodec.ParseUserName(value));
    }

    [Theory]
    [InlineData("corp.local", "ivanov")]
    [InlineData("corp.local", @"a\b")]
    [InlineData("", "ivanov@corp.local")]
    [InlineData("КОРП", "Иванов")]
    public void UserName_RoundTrips(string domain, string userName)
    {
        var built = CredentialRecordCodec.BuildUserName(domain, userName);

        var (parsedDomain, parsedUser) = CredentialRecordCodec.ParseUserName(built);

        Assert.Equal(domain.Length == 0 ? null : domain, parsedDomain);
        Assert.Equal(userName, parsedUser);
    }

    [Fact]
    public void BuildComment_WithDomainAndController()
    {
        Assert.Equal(
            "v=1;domain=corp.local;dc=dc01.corp.local",
            CredentialRecordCodec.BuildComment("corp.local", "dc01.corp.local"));
    }

    [Fact]
    public void BuildComment_WithoutController_OmitsDc()
    {
        Assert.Equal("v=1;domain=corp.local", CredentialRecordCodec.BuildComment("corp.local", null));
        Assert.Equal("v=1;domain=corp.local", CredentialRecordCodec.BuildComment("corp.local", ""));
    }

    [Fact]
    public void BuildComment_Empty_IsJustVersion()
    {
        Assert.Equal("v=1", CredentialRecordCodec.BuildComment(null, null));
        Assert.Equal("v=1", CredentialRecordCodec.BuildComment("", ""));
    }

    [Fact]
    public void BuildComment_EscapesSeparators()
    {
        var comment = CredentialRecordCodec.BuildComment("a;b=c", "dc;01");

        // Разделители значений не должны просочиться в комментарий: «;» и «=» встречаются только как служебные.
        Assert.Equal(3, comment.Count(c => c == ';') + 1);
        Assert.Equal(3, comment.Count(c => c == '='));
    }

    [Theory]
    [InlineData("corp.local", "dc01.corp.local")]
    [InlineData("corp.local", null)]
    [InlineData(null, "dc01.corp.local")]
    [InlineData(null, null)]
    [InlineData("a;b=c d", "dc;01=x%20y")]
    [InlineData("корп.локал", "дк01.корп.локал")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void Comment_RoundTrips(string? domain, string? controller)
    {
        var comment = CredentialRecordCodec.BuildComment(domain, controller);

        var (parsedDomain, parsedController) = CredentialRecordCodec.ParseComment(comment);

        Assert.Equal(domain, parsedDomain);
        Assert.Equal(controller, parsedController);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("какой-то чужой комментарий")]
    [InlineData(";;;")]
    [InlineData("=value;=")]
    [InlineData("domain=;dc=")]
    public void ParseComment_ForeignOrEmpty_GivesNothing(string? comment)
    {
        Assert.Equal((null, null), CredentialRecordCodec.ParseComment(comment));
    }

    [Fact]
    public void ParseComment_IgnoresUnknownKeysAndSpaces()
    {
        var (domain, controller) = CredentialRecordCodec.ParseComment("v=2; future=1 ; domain = corp.local ;dc=dc01");

        Assert.Equal("corp.local", domain);
        Assert.Equal("dc01", controller);
    }

    [Fact]
    public void ParseComment_BrokenEscape_DoesNotThrow()
    {
        var (domain, _) = CredentialRecordCodec.ParseComment("v=1;domain=%zz%");

        Assert.Equal("%zz%", domain);
    }

    [Fact]
    public void ParseComment_LastValueWins()
    {
        var (domain, _) = CredentialRecordCodec.ParseComment("v=1;domain=a.local;domain=b.local");

        Assert.Equal("b.local", domain);
    }

    [Theory]
    [InlineData("P@ssw0rd!")]
    [InlineData("Пароль-123")]
    [InlineData("с эмодзи \U0001F512")]
    [InlineData("a")]
    public void Password_RoundTripsAsUtf16(string password)
    {
        var bytes = CredentialRecordCodec.EncodePassword(password);

        Assert.Equal(password.Length * 2, bytes.Length);
        Assert.Equal(Encoding.Unicode.GetBytes(password), bytes);
        Assert.Equal(password, CredentialRecordCodec.DecodePassword(bytes));
    }

    [Fact]
    public void EncodedPassword_DoesNotAppearInComment()
    {
        const string password = "S3cret-Пароль";

        var comment = CredentialRecordCodec.BuildComment("corp.local", "dc01.corp.local");
        var userName = CredentialRecordCodec.BuildUserName("corp.local", "ivanov");

        Assert.DoesNotContain(password, comment);
        Assert.DoesNotContain(password, userName);
    }

    [Fact]
    public void Limits_MatchCredentialManagerConstants()
    {
        Assert.Equal(256, CredentialRecordCodec.MaxCommentLength);
        Assert.Equal(513, CredentialRecordCodec.MaxUserNameLength);
        Assert.Equal(2560, CredentialRecordCodec.MaxBlobSize);
        // Самый длинный пароль, который помещается в запись: 1280 символов UTF-16.
        Assert.Equal(CredentialRecordCodec.MaxBlobSize, CredentialRecordCodec.EncodePassword(new string('x', 1280)).Length);
    }
}
