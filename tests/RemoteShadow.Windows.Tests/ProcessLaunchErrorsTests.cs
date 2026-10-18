using RemoteShadow.Core;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class ProcessLaunchErrorsTests
{
    [Theory]
    [InlineData(1058)]
    [InlineData(1060)]
    [InlineData(1062)]
    public void SecondaryLogonServiceErrors_AreSecondaryLogonUnavailable(int code)
    {
        var ex = ProcessLaunchErrors.FromWin32(code, "mstsc.exe", "service error");

        Assert.Equal(OperationFailure.SecondaryLogonUnavailable, ex.Failure);
        Assert.Contains("Вторичный вход в систему", ex.Message);
        Assert.Contains("seclogon", ex.Message);
        Assert.EndsWith($"(код {code})", ex.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FileNotFound_IsNotFoundAndNamesProgram(int code)
    {
        var ex = ProcessLaunchErrors.FromWin32(code, "wt.exe", "The system cannot find the file specified.");

        Assert.Equal(OperationFailure.NotFound, ex.Failure);
        Assert.Contains("«wt.exe»", ex.Message);
    }

    [Fact]
    public void AccessDenied_IsAccessDenied()
    {
        var ex = ProcessLaunchErrors.FromWin32(5, "ssh.exe", "Access is denied.");

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("«ssh.exe»", ex.Message);
        Assert.EndsWith("(код 5)", ex.Message);
    }

    [Theory]
    [InlineData(1326)]
    [InlineData(1327)]
    [InlineData(1331)]
    [InlineData(1909)]
    public void RejectedAccount_IsAccessDeniedWithAccountHint(int code)
    {
        var ex = ProcessLaunchErrors.FromWin32(code, "mstsc.exe", "Logon failure.");

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("учётной записью", ex.Message);
        Assert.EndsWith($"(код {code})", ex.Message);
    }

    [Fact]
    public void ElevationRequired_AsksToRunAsAdministrator()
    {
        var ex = ProcessLaunchErrors.FromWin32(740, "tool.exe", null);

        Assert.Equal(OperationFailure.AccessDenied, ex.Failure);
        Assert.Contains("от имени администратора", ex.Message);
    }

    [Fact]
    public void UnknownCode_ShowsSystemTextAndCode()
    {
        var ex = ProcessLaunchErrors.FromWin32(193, "app.exe", "%1 is not a valid Win32 application.");

        Assert.Equal(OperationFailure.Other, ex.Failure);
        Assert.Contains("not a valid Win32 application", ex.Message);
        Assert.EndsWith("(код 193)", ex.Message);
    }

    [Fact]
    public void UnknownCode_WithoutText_StillReadable()
    {
        var ex = ProcessLaunchErrors.FromWin32(999999, "app.exe", " ");

        Assert.Contains("причина неизвестна", ex.Message);
    }

    [Fact]
    public void InnerException_IsKept()
    {
        var inner = new InvalidOperationException("inner");

        var ex = ProcessLaunchErrors.FromWin32(1058, "mstsc.exe", null, inner);

        Assert.Same(inner, ex.InnerException);
    }
}
