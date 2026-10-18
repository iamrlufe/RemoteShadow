using System.DirectoryServices.Protocols;
using System.Net.Sockets;
using RemoteShadow.Core.Models;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class LdapErrorMapperTests
{
    private static string AdMessage(string sub) =>
        $"80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data {sub}, v4563";

    [Theory]
    [InlineData("52e", LogonError.InvalidCredentials)]
    [InlineData("52E", LogonError.InvalidCredentials)]
    [InlineData("525", LogonError.InvalidCredentials)]
    [InlineData("775", LogonError.AccountLocked)]
    [InlineData("533", LogonError.AccountDisabled)]
    [InlineData("701", LogonError.AccountDisabled)]
    [InlineData("532", LogonError.PasswordExpired)]
    [InlineData("773", LogonError.PasswordExpired)]
    [InlineData("530", LogonError.Unknown)]
    [InlineData("531", LogonError.Unknown)]
    public void Code49_MapsAdSubcode(string sub, LogonError expected)
    {
        var ex = new LdapException(49, "The supplied credential is invalid.", AdMessage(sub));

        var failure = LdapErrorMapper.Map(ex, "dc01.corp.local");

        Assert.Equal(expected, failure.Error);
        Assert.Contains("(LDAP 49", failure.Message);
    }

    [Fact]
    public void Code49_WithoutSubcode_IsInvalidCredentials()
    {
        var failure = LdapErrorMapper.MapLdap(49, null, "The supplied credential is invalid.");

        Assert.Equal(LogonError.InvalidCredentials, failure.Error);
        Assert.Contains("логин или пароль", failure.Message);
    }

    [Fact]
    public void Code49_UnknownSubcode_IsInvalidCredentials()
    {
        var failure = LdapErrorMapper.MapLdap(49, AdMessage("999"), null);

        Assert.Equal(LogonError.InvalidCredentials, failure.Error);
    }

    [Fact]
    public void AccountLocked_MessageSaysWhatToDo()
    {
        var failure = LdapErrorMapper.MapLdap(49, AdMessage("775"), null);

        Assert.Contains("заблокирована", failure.Message);
        Assert.Contains("администратор", failure.Message);
        Assert.EndsWith("(LDAP 49, 775)", failure.Message);
    }

    [Theory]
    [InlineData(81)]
    [InlineData(85)]
    [InlineData(91)]
    [InlineData(52)]
    public void ServerDownTimeoutConnect_AreUnreachable(int code)
    {
        var failure = LdapErrorMapper.MapLdap(code, null, "The LDAP server is unavailable.", "dc01.corp.local");

        Assert.Equal(LogonError.DomainControllerUnreachable, failure.Error);
        Assert.Contains("dc01.corp.local", failure.Message);
        Assert.Contains("VPN", failure.Message);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NoData)]
    [InlineData(SocketError.TryAgain)]
    public void DnsSocketErrors_AreDnsFailure(SocketError error)
    {
        var failure = LdapErrorMapper.Map(new SocketException((int)error), "corp.local");

        Assert.Equal(LogonError.DnsFailure, failure.Error);
        Assert.Contains("DNS", failure.Message);
        Assert.Contains("VPN", failure.Message);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    public void OtherSocketErrors_AreUnreachable(SocketError error)
    {
        var failure = LdapErrorMapper.Map(new SocketException((int)error), "dc01");

        Assert.Equal(LogonError.DomainControllerUnreachable, failure.Error);
    }

    [Fact]
    public void Timeout_IsUnreachable()
    {
        var failure = LdapErrorMapper.Map(new TimeoutException(), "dc01");

        Assert.Equal(LogonError.DomainControllerUnreachable, failure.Error);
    }

    [Fact]
    public void LocalError_IsUnknownWithTimeHint()
    {
        var failure = LdapErrorMapper.MapLdap(82, null, "Local error");

        Assert.Equal(LogonError.Unknown, failure.Error);
        Assert.Contains("время", failure.Message);
    }

    [Fact]
    public void OtherCode_IsUnknownWithText()
    {
        var failure = LdapErrorMapper.Map(new LdapException(1, "Operations error", "000004DC: LdapErr: DSID-0C090A37"), "dc01");

        Assert.Equal(LogonError.Unknown, failure.Error);
        Assert.Contains("Operations error", failure.Message);
        Assert.Contains("(LDAP 1)", failure.Message);
    }

    [Fact]
    public void UnexpectedException_IsUnknown()
    {
        var failure = LdapErrorMapper.Map(new InvalidOperationException("что-то сломалось"));

        Assert.Equal(LogonError.Unknown, failure.Error);
        Assert.Contains("что-то сломалось", failure.Message);
    }

    [Theory]
    [InlineData("80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 52e, v4563", "52e")]
    [InlineData("... AcceptSecurityContext error, data 775, v1db1", "775")]
    [InlineData("... data 52E ...", "52e")]
    [InlineData("no subcode here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractAdSubCode(string? message, string? expected)
    {
        Assert.Equal(expected, LdapErrorMapper.ExtractAdSubCode(message));
    }

    [Theory]
    [InlineData("52e")]
    [InlineData("525")]
    [InlineData("999")]
    [InlineData(null)]
    public void CurrentUser_InvalidCredentials_DoesNotBlameThePassword(string? sub)
    {
        var failure = LdapErrorMapper.MapLdap(49, sub is null ? null : AdMessage(sub), null, "dc01.corp.local", currentUser: true);

        Assert.Equal(LogonError.InvalidCredentials, failure.Error);
        Assert.Contains("текущую учётную запись Windows", failure.Message);
        Assert.DoesNotContain("Caps Lock", failure.Message);
        Assert.Contains("(LDAP 49", failure.Message);
    }

    [Theory]
    [InlineData("775", LogonError.AccountLocked)]
    [InlineData("533", LogonError.AccountDisabled)]
    [InlineData("532", LogonError.PasswordExpired)]
    public void CurrentUser_SpecificAccountStates_KeepTheirMessages(string sub, LogonError expected)
    {
        var failure = LdapErrorMapper.Map(new LdapException(49, "x", AdMessage(sub)), "dc01", currentUser: true);

        Assert.Equal(expected, failure.Error);
    }

    [Fact]
    public void CurrentUser_NetworkErrors_AreUnchanged()
    {
        var plain = LdapErrorMapper.MapLdap(81, null, null, "dc01");
        var current = LdapErrorMapper.MapLdap(81, null, null, "dc01", currentUser: true);

        Assert.Equal(plain, current);
    }

    [Fact]
    public void ExplicitMode_KeepsLoginAndPasswordWording()
    {
        var failure = LdapErrorMapper.MapLdap(49, AdMessage("52e"), null, "dc01", currentUser: false);

        Assert.Contains("Неверный логин или пароль", failure.Message);
    }
}
