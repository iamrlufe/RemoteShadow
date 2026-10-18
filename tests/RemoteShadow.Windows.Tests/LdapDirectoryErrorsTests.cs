using System.DirectoryServices.Protocols;
using RemoteShadow.Core;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class LdapDirectoryErrorsTests
{
    private const string UserDn = "CN=Иванов И.,OU=Склад,DC=corp,DC=local";
    private const string GroupDn = "CN=VPN-Users,OU=Группы,DC=corp,DC=local";

    private static DirectoryOperationInfo Op(DirectoryOperation operation, bool encrypted = true) =>
        new(operation, UserDn, operation is DirectoryOperation.AddToGroup or DirectoryOperation.RemoveFromGroup ? GroupDn : null, encrypted);

    private static DirectoryOperation Parse(string name) => Enum.Parse<DirectoryOperation>(name);

    private static OperationFailedException Map(int code, DirectoryOperation operation, string? server = null, bool encrypted = true) =>
        LdapDirectoryErrors.FromResult(code, server, "text", Op(operation, encrypted));

    [Fact]
    public void AccessDenied_ResetPassword_NamesRightAndObject()
    {
        var ex = Map(50, DirectoryOperation.ResetPassword);

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("сброс пароля", ex.Message);
        Assert.Contains("Reset password", ex.Message);
        Assert.Contains("«Иванов И.»", ex.Message);
        Assert.Contains("OU=Склад,DC=corp,DC=local", ex.Message);
        Assert.EndsWith("(LDAP 50)", ex.Message);
    }

    [Theory]
    [InlineData("Unlock", "lockoutTime")]
    [InlineData("SetEnabled", "userAccountControl")]
    [InlineData("RequirePasswordChange", "pwdLastSet")]
    public void AccessDenied_NamesThePropertyThatCannotBeWritten(string operationName, string property)
    {
        var ex = Map(50, Parse(operationName));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains(property, ex.Message);
        Assert.Contains("«Иванов И.»", ex.Message);
    }

    [Theory]
    [InlineData("AddToGroup")]
    [InlineData("RemoveFromGroup")]
    public void AccessDenied_GroupChange_NamesGroupAndMemberProperty(string operationName)
    {
        var ex = Map(50, Parse(operationName));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("«VPN-Users»", ex.Message);
        Assert.Contains("member", ex.Message);
    }

    [Fact]
    public void AccessDenied_Read_SaysReadRightIsMissing()
    {
        var ex = Map(50, DirectoryOperation.Read);

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("чтения", ex.Message);
    }

    [Fact]
    public void AccessDenied_UnknownObject_DoesNotCrash()
    {
        var ex = LdapDirectoryErrors.FromResult(50, null, null, new DirectoryOperationInfo(DirectoryOperation.ResetPassword));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("«объект»", ex.Message);
    }

    [Fact]
    public void ConstraintViolation_OnResetPassword_IsPolicyViolation()
    {
        var ex = Map(19, DirectoryOperation.ResetPassword);

        Assert.Equal(OperationFailure.PolicyViolation, ex.Failure);
        Assert.Contains("политику", ex.Message);
        Assert.EndsWith("(LDAP 19)", ex.Message);
    }

    [Fact]
    public void UnwillingToPerform_WithPasswordRestrictionCode_IsPolicyViolation()
    {
        var ex = Map(53, DirectoryOperation.ResetPassword, "0000052D: SvcErr: DSID-031A12D2, problem 5003 (WILL_NOT_PERFORM), data 0");

        Assert.Equal(OperationFailure.PolicyViolation, ex.Failure);
        Assert.Contains("политику", ex.Message);
        Assert.EndsWith("(LDAP 53, 0000052D)", ex.Message);
    }

    [Fact]
    public void UnwillingToPerform_OnUnencryptedChannel_ExplainsEncryption()
    {
        var ex = Map(53, DirectoryOperation.ResetPassword, null, encrypted: false);

        Assert.Equal(OperationFailure.PolicyViolation, ex.Failure);
        Assert.Contains("шифрованному", ex.Message);
    }

    [Fact]
    public void UnwillingToPerform_WithGeneralFailureCode_ExplainsEncryption()
    {
        var ex = Map(53, DirectoryOperation.ResetPassword, "0000001F: SvcErr: DSID-031A1248, problem 5003 (WILL_NOT_PERFORM), data 0");

        Assert.Contains("шифрованному", ex.Message);
    }

    [Fact]
    public void UnwillingToPerform_Unexplained_SaysPolicyOrForbiddenReset()
    {
        var ex = Map(53, DirectoryOperation.ResetPassword);

        Assert.Equal(OperationFailure.PolicyViolation, ex.Failure);
        Assert.Contains("отказался установить пароль", ex.Message);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(13)]
    public void ConfidentialityRequired_OnResetPassword_ExplainsEncryption(int code)
    {
        var ex = Map(code, DirectoryOperation.ResetPassword);

        Assert.Contains("шифрованному", ex.Message);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(53)]
    public void RequirePasswordChange_Rejected_MentionsNeverExpires(int code)
    {
        var ex = Map(code, DirectoryOperation.RequirePasswordChange);

        Assert.Equal(OperationFailure.PolicyViolation, ex.Failure);
        Assert.Contains("Срок действия пароля не ограничен", ex.Message);
        Assert.DoesNotContain("не прошёл политику", ex.Message);
    }

    [Fact]
    public void NoSuchObject_IsNotFound_AndNamesObject()
    {
        var ex = Map(32, DirectoryOperation.Unlock);

        Assert.Equal(OperationFailure.NotFound, ex.Failure);
        Assert.Contains("«Иванов И.»", ex.Message);
        Assert.Contains("обновите список", ex.Message);
    }

    [Fact]
    public void NoSuchObject_OnGroupOnly_NamesGroup()
    {
        var ex = LdapDirectoryErrors.FromResult(
            32, null, null, new DirectoryOperationInfo(DirectoryOperation.AddToGroup, null, GroupDn));

        Assert.Equal(OperationFailure.NotFound, ex.Failure);
        Assert.Contains("«VPN-Users»", ex.Message);
    }

    [Theory]
    [InlineData(68, null)]
    [InlineData(20, null)]
    [InlineData(53, "00000562: UpdErr: DSID-031A11C8, problem 5003 (WILL_NOT_PERFORM), data 0")]
    [InlineData(19, "00000562: SvcErr: DSID-031A11C8, problem 5003, data 0")]
    public void AddToGroup_AlreadyMember_SaysSo(int code, string? server)
    {
        var ex = Map(code, DirectoryOperation.AddToGroup, server);

        Assert.Contains("уже состоит в группе", ex.Message);
        Assert.Contains("«Иванов И.»", ex.Message);
        Assert.Contains("«VPN-Users»", ex.Message);
    }

    [Theory]
    [InlineData(16, null)]
    [InlineData(53, "00000561: UpdErr: DSID-031A11C8, problem 5003 (WILL_NOT_PERFORM), data 0")]
    public void RemoveFromGroup_NotMember_SaysSo(int code, string? server)
    {
        var ex = Map(code, DirectoryOperation.RemoveFromGroup, server);

        Assert.Contains("не состоит в группе", ex.Message);
    }

    [Fact]
    public void Add_NoSuchAttribute_IsNotTreatedAsNotMember()
    {
        var ex = Map(16, DirectoryOperation.AddToGroup);

        Assert.DoesNotContain("не состоит", ex.Message);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(85)]
    public void Timeouts_AreTimeout(int code)
    {
        Assert.Equal(OperationFailure.Timeout, Map(code, DirectoryOperation.Read).Failure);
    }

    [Theory]
    [InlineData(52)]
    [InlineData(81)]
    [InlineData(91)]
    public void ServerUnavailable_IsUnreachable(int code)
    {
        var ex = Map(code, DirectoryOperation.Read);

        Assert.Equal(OperationFailure.Unreachable, ex.Failure);
        Assert.Contains("VPN", ex.Message);
    }

    [Fact]
    public void InvalidDnSyntax_IsInvalidInput()
    {
        Assert.Equal(OperationFailure.InvalidInput, Map(34, DirectoryOperation.AddToGroup).Failure);
    }

    [Fact]
    public void InvalidCredentials_AsksToLogOnAgain()
    {
        var ex = Map(49, DirectoryOperation.Read);

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("Войдите в домен заново", ex.Message);
    }

    [Fact]
    public void UnknownCode_ShowsServerTextAndCode()
    {
        var ex = LdapDirectoryErrors.FromResult(
            1, "Operations error occurred.", "ignored", Op(DirectoryOperation.Read));

        Assert.Equal(OperationFailure.Other, ex.Failure);
        Assert.Contains("Operations error occurred", ex.Message);
        Assert.EndsWith("(LDAP 1)", ex.Message);
    }

    [Fact]
    public void UnknownCode_WithoutAnyText_StillReadable()
    {
        var ex = LdapDirectoryErrors.FromResult(1, null, "  ", Op(DirectoryOperation.Read));

        Assert.Contains("причина неизвестна", ex.Message);
    }

    [Fact]
    public void LongServerText_IsTruncated()
    {
        var ex = LdapDirectoryErrors.FromResult(1, new string('x', 2000), null, Op(DirectoryOperation.Read));

        Assert.True(ex.Message.Length < 400);
        Assert.Contains("…", ex.Message);
    }

    [Theory]
    [InlineData("0000052D: SvcErr: DSID-031A12D2, problem 5003", 0x52D)]
    [InlineData("  00002081: AtrErr", 0x2081)]
    [InlineData("0000001f: x", 0x1F)]
    [InlineData("LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 52e", null)]
    [InlineData("052D: short", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractWin32Code(string? message, int? expected)
    {
        Assert.Equal(expected, LdapDirectoryErrors.ExtractWin32Code(message));
    }

    [Fact]
    public void Translate_ReadyException_IsReturnedAsIs()
    {
        var ready = new OperationFailedException("готово", OperationFailure.NotLoggedIn);

        Assert.Same(ready, LdapDirectoryErrors.Translate(ready, Op(DirectoryOperation.Read)));
    }

    [Fact]
    public void Translate_LdapException_UsesCodeAndServerMessage()
    {
        var ldap = new LdapException(50, "Insufficient access", "00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS)");

        var ex = LdapDirectoryErrors.Translate(ldap, Op(DirectoryOperation.Unlock));

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Same(ldap, ex.InnerException);
        Assert.Contains("lockoutTime", ex.Message);
    }

    [Fact]
    public void Translate_TimeoutException_IsTimeout()
    {
        var ex = LdapDirectoryErrors.Translate(new TimeoutException(), Op(DirectoryOperation.Read));

        Assert.Equal(OperationFailure.Timeout, ex.Failure);
    }

    [Fact]
    public void Translate_OtherException_IsOtherWithText()
    {
        var ex = LdapDirectoryErrors.Translate(new InvalidOperationException("странный сбой"), Op(DirectoryOperation.Read));

        Assert.Equal(OperationFailure.Other, ex.Failure);
        Assert.Contains("странный сбой", ex.Message);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("ResetPassword")]
    [InlineData("RequirePasswordChange")]
    [InlineData("Unlock")]
    [InlineData("SetEnabled")]
    [InlineData("AddToGroup")]
    [InlineData("RemoveFromGroup")]
    public void EveryMessage_EndsWithLdapCodeInParentheses(string operationName)
    {
        var operation = Parse(operationName);
        foreach (var code in new[] { 19, 32, 34, 50, 53, 81, 85, 1 })
        {
            var message = Map(code, operation).Message;

            Assert.Matches(@"\(LDAP \d+(, [0-9A-F]{8})?\)$", message);
        }
    }

    [Fact]
    public void Messages_AreRussian_AndNeverContainRawEnumNames()
    {
        foreach (var operation in Enum.GetValues<DirectoryOperation>())
        {
            var message = Map(50, operation).Message;

            Assert.Matches("[А-Яа-я]{4}", message);
            Assert.DoesNotContain(nameof(DirectoryOperation), message);
        }
    }
}
