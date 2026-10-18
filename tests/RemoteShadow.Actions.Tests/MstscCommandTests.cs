using RemoteShadow.Core;
using RemoteShadow.Modules;

namespace RemoteShadow.Actions.Tests;

public class MstscCommandTests
{
    private const string Host = "pc-acc-014.corp.local";

    [Fact]
    public void Shadow_view_with_consent()
    {
        Assert.Equal(["/v:" + Host, "/shadow:3"], MstscCommand.Shadow(Host, 3, control: false, noConsent: false));
    }

    [Fact]
    public void Shadow_control_with_consent()
    {
        Assert.Equal(["/v:" + Host, "/shadow:3", "/control"], MstscCommand.Shadow(Host, 3, control: true, noConsent: false));
    }

    [Fact]
    public void Shadow_view_without_consent()
    {
        Assert.Equal(["/v:" + Host, "/shadow:3", "/noConsentPrompt"], MstscCommand.Shadow(Host, 3, control: false, noConsent: true));
    }

    [Fact]
    public void Shadow_control_without_consent_keeps_order()
    {
        Assert.Equal(
            ["/v:" + Host, "/shadow:3", "/control", "/noConsentPrompt"],
            MstscCommand.Shadow(Host, 3, control: true, noConsent: true));
    }

    [Theory]
    [InlineData(0, "/shadow:0")]
    [InlineData(1, "/shadow:1")]
    [InlineData(65536, "/shadow:65536")]
    public void Shadow_accepts_zero_and_positive_session_ids(int id, string expected)
    {
        Assert.Equal(expected, MstscCommand.Shadow(Host, id, false, false)[1]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Shadow_rejects_negative_session_id(int id)
    {
        var ex = Assert.Throws<OperationFailedException>(() => MstscCommand.Shadow(Host, id, false, false));
        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
    }

    [Fact]
    public void Rdp_has_only_host()
    {
        Assert.Equal(["/v:" + Host], MstscCommand.Rdp(Host));
    }

    [Theory]
    [InlineData("PC1")]
    [InlineData("pc-1.corp.local")]
    [InlineData("10.0.0.5")]
    public void Valid_hosts_pass_through_unchanged(string host)
    {
        Assert.Equal("/v:" + host, MstscCommand.Rdp(host)[0]);
        Assert.Equal("/v:" + host, MstscCommand.Shadow(host, 1, false, false)[0]);
    }

    [Theory]
    [MemberData(nameof(TestData.BadHostData), MemberType = typeof(TestData))]
    public void Bad_hosts_are_rejected(string host)
    {
        var shadow = Assert.Throws<OperationFailedException>(() => MstscCommand.Shadow(host, 1, true, true));
        var rdp = Assert.Throws<OperationFailedException>(() => MstscCommand.Rdp(host));
        Assert.Equal(OperationFailure.InvalidInput, shadow.Failure);
        Assert.Equal(OperationFailure.InvalidInput, rdp.Failure);
    }

    [Fact]
    public void Null_host_is_rejected()
    {
        Assert.Throws<OperationFailedException>(() => MstscCommand.Rdp(null!));
        Assert.Throws<OperationFailedException>(() => MstscCommand.Shadow(null!, 1, false, false));
    }

    [Fact]
    public void Program_names_are_constants()
    {
        Assert.Equal("mstsc.exe", MstscCommand.MstscExe);
        Assert.Equal("ssh.exe", MstscCommand.SshExe);
        Assert.Equal("wt.exe", MstscCommand.WindowsTerminalExe);
        Assert.Equal("explorer.exe", MstscCommand.ExplorerExe);
    }
}
