using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts.Tests;

public class WtsErrorMapperTests
{
    [Theory]
    [InlineData(1722)]
    [InlineData(53)]
    [InlineData(67)]
    [InlineData(1231)]
    [InlineData(1225)]
    public void Unreachable_codes(int code)
    {
        var failure = WtsErrorMapper.MapQuery(code, "pc1.corp.local");

        Assert.Equal(HostQueryStatus.Unreachable, failure.Status);
        Assert.Equal(OperationFailure.Unreachable, failure.Failure);
        Assert.Contains("pc1.corp.local", failure.Message);
        Assert.EndsWith($"(код {code}).", failure.Message);
    }

    [Fact]
    public void Access_denied_says_admin_rights_missing()
    {
        var failure = WtsErrorMapper.MapQuery(5, "pc1");

        Assert.Equal(HostQueryStatus.AccessDenied, failure.Status);
        Assert.Equal(OperationFailure.AccessDenied, failure.Failure);
        Assert.StartsWith("Нет прав администратора на компьютере pc1", failure.Message);
    }

    [Theory]
    [InlineData(1460)]
    [InlineData(258)]
    public void Timeout_codes(int code)
    {
        var failure = WtsErrorMapper.MapQuery(code, "pc1");

        Assert.Equal(HostQueryStatus.Timeout, failure.Status);
        Assert.Equal(OperationFailure.Timeout, failure.Failure);
        Assert.Contains("не отвечает", failure.Message);
    }

    [Fact]
    public void Unknown_code_is_error_with_text_and_code()
    {
        var failure = WtsErrorMapper.MapQuery(87, "pc1");

        Assert.Equal(HostQueryStatus.Error, failure.Status);
        Assert.Equal(OperationFailure.Other, failure.Failure);
        Assert.Contains("pc1", failure.Message);
        Assert.EndsWith("(код 87).", failure.Message);
    }

    [Theory]
    [InlineData(1722, HostQueryStatus.Unreachable)]
    [InlineData(5, HostQueryStatus.AccessDenied)]
    [InlineData(1460, HostQueryStatus.Timeout)]
    [InlineData(0, HostQueryStatus.Error)]
    [InlineData(7022, HostQueryStatus.Error)]
    public void ToStatus(int code, HostQueryStatus expected) =>
        Assert.Equal(expected, WtsErrorMapper.ToStatus(code));

    [Fact]
    public void Operation_message_names_operation_and_host()
    {
        var failure = WtsErrorMapper.MapOperation("завершить сессию 3", 1722, "pc1");

        Assert.Equal(OperationFailure.Unreachable, failure.Failure);
        Assert.StartsWith("Не удалось завершить сессию 3 на компьютере pc1:", failure.Message);
        Assert.EndsWith("(код 1722).", failure.Message);
    }

    [Fact]
    public void Operation_access_denied_is_admin_rights()
    {
        var failure = WtsErrorMapper.MapOperation("отключить сессию 2", 5, "pc1");

        Assert.Equal(OperationFailure.AccessDenied, failure.Failure);
        Assert.Contains("нет прав администратора", failure.Message);
    }

    [Fact]
    public void Shutdown_access_denied_names_remote_shutdown_privilege()
    {
        var failure = WtsErrorMapper.MapShutdown("перезагрузить", 5, "pc1");

        Assert.Equal(OperationFailure.AccessDenied, failure.Failure);
        Assert.Contains("SeRemoteShutdownPrivilege", failure.Message);
        Assert.Contains("pc1", failure.Message);
        Assert.EndsWith("(код 5).", failure.Message);
    }

    [Fact]
    public void Shutdown_in_progress_is_friendly()
    {
        var failure = WtsErrorMapper.MapShutdown("выключить", 1115, "pc1");

        Assert.Equal(OperationFailure.Other, failure.Failure);
        Assert.Contains("уже выполняется", failure.Message);
    }

    [Fact]
    public void Shutdown_other_codes_use_generic_mapping()
    {
        var failure = WtsErrorMapper.MapShutdown("выключить", 1722, "pc1");

        Assert.Equal(OperationFailure.Unreachable, failure.Failure);
    }

    [Fact]
    public void WtsException_keeps_code_and_api_without_user_text()
    {
        var ex = new WtsException("WTSEnumerateSessionsExW", 1722);

        Assert.Equal(1722, ex.ErrorCode);
        Assert.Equal("WTSEnumerateSessionsExW", ex.Api);
    }
}
