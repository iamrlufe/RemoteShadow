using System.Text;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class LdapValueParserTests
{
    [Fact]
    public void FileTime_RoundTripsKnownDate()
    {
        var expected = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var parsed = LdapValueParser.ParseFileTime(expected.ToFileTimeUtc().ToString());

        Assert.Equal(expected, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Fact]
    public void FileTime_Epoch1601Plus1Second()
    {
        Assert.Equal(new DateTime(1601, 1, 1, 0, 0, 1, DateTimeKind.Utc), LdapValueParser.ParseFileTime("10000000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775806")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public void FileTime_NeverOrInvalid_IsNull(string? value)
    {
        Assert.Null(LdapValueParser.ParseFileTime(value));
    }

    [Fact]
    public void GeneralizedTime_Standard()
    {
        var parsed = LdapValueParser.ParseGeneralizedTime("20260115103000.0Z");

        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Fact]
    public void GeneralizedTime_WithoutFraction()
    {
        Assert.Equal(
            new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            LdapValueParser.ParseGeneralizedTime("20261231235959Z"));
    }

    [Fact]
    public void GeneralizedTime_Fraction()
    {
        Assert.Equal(
            new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc).AddMilliseconds(500),
            LdapValueParser.ParseGeneralizedTime("20260115103000.5Z"));
        Assert.Equal(
            new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc).AddMilliseconds(250),
            LdapValueParser.ParseGeneralizedTime("20260115103000,25Z"));
    }

    [Theory]
    [InlineData("20260115133000+0300")]
    [InlineData("20260115133000+03")]
    [InlineData("20260115053000-0500")]
    [InlineData("20260115160000+0530")]
    public void GeneralizedTime_Offset_IsConvertedToUtc(string value)
    {
        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc), LdapValueParser.ParseGeneralizedTime(value));
    }

    [Fact]
    public void GeneralizedTime_WithoutSuffix_IsUtc()
    {
        Assert.Equal(
            new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            LdapValueParser.ParseGeneralizedTime("20260115103000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("20261315103000.0Z")]
    [InlineData("20260132103000.0Z")]
    [InlineData("20260115253000.0Z")]
    [InlineData("20260115103000.0X")]
    [InlineData("2026011510300a.0Z")]
    [InlineData("20260115103000+9")]
    [InlineData("20260115103000Zjunk")]
    public void GeneralizedTime_Invalid_IsNull(string? value)
    {
        Assert.Null(LdapValueParser.ParseGeneralizedTime(value));
    }

    [Theory]
    [InlineData("512", 512)]
    [InlineData("66048", 66048)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("2147483648", int.MinValue)]
    public void ParseInt_Numbers(string value, int expected)
    {
        Assert.Equal(expected, LdapValueParser.ParseInt(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")]
    public void ParseInt_NotNumber_IsNull(string? value)
    {
        Assert.Null(LdapValueParser.ParseInt(value));
    }
}

public class UserAccountControlTests
{
    [Theory]
    [InlineData(512, false)]
    [InlineData(514, true)]
    [InlineData(66050, true)]
    [InlineData(0, false)]
    public void IsDisabled(int flags, bool expected)
    {
        Assert.Equal(expected, UserAccountControl.IsDisabled(flags));
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(66048, true)]
    [InlineData(66050, true)]
    public void NeverExpires(int flags, bool expected)
    {
        Assert.Equal(expected, UserAccountControl.NeverExpires(flags));
    }

    [Theory]
    [InlineData(0x10, true)]
    [InlineData(0x0, false)]
    [InlineData(0x800010, true)]
    [InlineData(0x800000, false)]
    public void IsLockedOut(int flags, bool expected)
    {
        Assert.Equal(expected, UserAccountControl.IsLockedOut(flags));
    }

    [Theory]
    [InlineData(514, true, 512)]
    [InlineData(512, true, 512)]
    [InlineData(512, false, 514)]
    [InlineData(514, false, 514)]
    [InlineData(66050, true, 66048)]
    [InlineData(66048, false, 66050)]
    public void WithEnabled_ChangesOnlyDisableBit(int flags, bool enabled, int expected)
    {
        Assert.Equal(expected, UserAccountControl.WithEnabled(flags, enabled));
    }
}

public class UnicodePwdEncoderTests
{
    [Theory]
    [InlineData("Pa$$w0rd")]
    [InlineData("Пароль-123!")]
    [InlineData("a")]
    [InlineData("quote\"inside")]
    [InlineData("emoji\U0001F600x")]
    public void Encode_IsQuotedUtf16LeWithoutBomOrTerminator(string password)
    {
        var expected = Encoding.Unicode.GetBytes("\"" + password + "\"");

        var actual = UnicodePwdEncoder.Encode(password);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Encode_KnownBytes()
    {
        // "ab" в кавычках: 22 00 61 00 62 00 22 00
        Assert.Equal(new byte[] { 0x22, 0, 0x61, 0, 0x62, 0, 0x22, 0 }, UnicodePwdEncoder.Encode("ab"));
    }

    [Fact]
    public void Encode_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => UnicodePwdEncoder.Encode(string.Empty));
    }

    [Fact]
    public void Encode_Null_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => UnicodePwdEncoder.Encode(null!));
    }
}
