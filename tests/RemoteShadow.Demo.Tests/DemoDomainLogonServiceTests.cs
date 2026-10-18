using System.Diagnostics;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Demo.Tests;

public class DemoDomainLogonServiceTests
{
    private static DemoDomainLogonService Service(bool joined = true) =>
        new(isDomainJoined: joined, timings: DemoTimings.Instant);

    private static LogonRequest Explicit(string? user, string? password, string? domain = "corp.local", string? controller = null) =>
        new(LogonMode.Explicit, domain, controller, user, password);

    private static Task<LogonResult> Logon(DemoDomainLogonService service, LogonRequest request) =>
        service.LogonAsync(request, CancellationToken.None);

    // ---------- окружение ----------

    [Fact]
    public void DetectEnvironment_Defaults_AreDomainJoinedCorpLocal()
    {
        var environment = new DemoDomainLogonService().DetectEnvironment();

        Assert.True(environment.IsDomainJoined);
        Assert.Equal("corp.local", environment.DomainName);
        Assert.Equal("DEMO-PC", environment.ComputerName);
        Assert.Equal("demo-admin", environment.CurrentUser);
    }

    [Fact]
    public void DetectEnvironment_IsConfigurable()
    {
        var service = new DemoDomainLogonService(false, "test.local", "LAPTOP-7", "ivanov");

        var environment = service.DetectEnvironment();

        Assert.False(environment.IsDomainJoined);
        Assert.Null(environment.DomainName);
        Assert.Equal("LAPTOP-7", environment.ComputerName);
        Assert.Equal("ivanov", environment.CurrentUser);

        var joined = new DemoDomainLogonService(true, "test.local").DetectEnvironment();
        Assert.Equal("test.local", joined.DomainName);
    }

    [Fact]
    public void Constructor_EmptyDomain_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new DemoDomainLogonService(domainName: " "));
    }

    // ---------- текущий пользователь ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentUser_AlwaysSucceeds(bool joined)
    {
        var result = await Logon(Service(joined), new LogonRequest(LogonMode.CurrentUser, null, null, null, null));

        Assert.True(result.Success);
        Assert.Equal(LogonError.None, result.Error);
        Assert.Null(result.Message);
        Assert.False(result.IsPrivilegedAccount);

        using var connection = result.Connection!;
        Assert.Equal("CORP\\demo-admin", connection.AccountName);
        Assert.Equal("corp.local", connection.DomainName);
        Assert.Equal("DC01.corp.local", connection.DomainController);
        Assert.False(connection.UsesExplicitCredentials);
        Assert.Null(connection.Credential);
        Assert.Equal("Kerberos (демо)", connection.AuthenticationKind);
    }

    [Fact]
    public async Task CurrentUser_UsesConfiguredUser_AndMayBePrivileged()
    {
        var service = new DemoDomainLogonService(currentUser: "da-boss", timings: DemoTimings.Instant);

        var result = await Logon(service, new LogonRequest(LogonMode.CurrentUser, null, null, null, null));

        Assert.True(result.Success);
        Assert.True(result.IsPrivilegedAccount);
        Assert.Equal("CORP\\da-boss", result.Connection!.AccountName);
    }

    // ---------- явные данные: успех ----------

    [Fact]
    public async Task Explicit_DemoPassword_Succeeds()
    {
        var result = await Logon(Service(), Explicit("ivanov", "demo", controller: "DC02.corp.local"));

        Assert.True(result.Success);
        Assert.Equal(LogonError.None, result.Error);
        Assert.False(result.IsPrivilegedAccount);

        using var connection = result.Connection!;
        Assert.Equal("CORP\\ivanov", connection.AccountName);
        Assert.True(connection.UsesExplicitCredentials);
        Assert.Equal("DC02.corp.local", connection.DomainController);
        Assert.Equal("corp.local", connection.DomainName);
        Assert.NotNull(connection.Credential);
        Assert.Equal("ivanov", connection.Credential!.UserName);
        Assert.Equal("CORP", connection.Credential.Domain);
    }

    [Theory]
    [InlineData("da-ivanov", true)]
    [InlineData("DA-ivanov", true)]
    [InlineData("adm-ivanov", false)]
    [InlineData("ivanov-da-", false)]
    public async Task Explicit_PrivilegedOnlyForDaPrefix(string login, bool privileged)
    {
        var result = await Logon(Service(), Explicit(login, "demo"));

        Assert.True(result.Success);
        Assert.Equal(privileged, result.IsPrivilegedAccount);
    }

    [Theory]
    [InlineData("CORP\\ivanov", "corp.local")]
    [InlineData("corp\\ivanov", null)]
    [InlineData("ivanov@corp.local", null)]
    [InlineData("ivanov@CORP.LOCAL", "")]
    [InlineData("ivanov", "CORP.LOCAL")]
    [InlineData("ivanov", "CORP")]
    [InlineData("ivanov", null)]
    [InlineData("  ivanov  ", " corp.local ")]
    public async Task Explicit_AcceptsAllLoginForms(string login, string? domain)
    {
        var result = await Logon(Service(), Explicit(login, "demo", domain));

        Assert.True(result.Success, result.Message);
        Assert.Equal("CORP\\ivanov", result.Connection!.AccountName);
    }

    // ---------- явные данные: ошибки ----------

    [Theory]
    [InlineData("locked", "demo")]
    [InlineData("locked", "wrong")]
    [InlineData("LOCKED", null)]
    public async Task Explicit_LockedLogin_IsAccountLocked(string login, string? password)
    {
        var result = await Logon(Service(), Explicit(login, password));

        Assert.False(result.Success);
        Assert.Null(result.Connection);
        Assert.Equal(LogonError.AccountLocked, result.Error);
        Assert.Contains("заблокирована", result.Message);
    }

    [Theory]
    [InlineData("disabled", "demo")]
    [InlineData("Disabled", "oops")]
    public async Task Explicit_DisabledLogin_IsAccountDisabled(string login, string password)
    {
        var result = await Logon(Service(), Explicit(login, password));

        Assert.False(result.Success);
        Assert.Equal(LogonError.AccountDisabled, result.Error);
        Assert.Contains("отключена", result.Message);
    }

    [Fact]
    public async Task Explicit_ExpiredAndUnreachableLogins_ShowTheirErrors()
    {
        var expired = await Logon(Service(), Explicit("expired", "demo"));
        var unreachable = await Logon(Service(), Explicit("unreachable", "demo"));

        Assert.Equal(LogonError.PasswordExpired, expired.Error);
        Assert.Equal(LogonError.DomainControllerUnreachable, unreachable.Error);
        Assert.False(expired.Success);
        Assert.False(unreachable.Success);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("Demo")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Explicit_WrongPassword_IsInvalidCredentials_AndDoesNotEchoPassword(string? password)
    {
        var result = await Logon(Service(), Explicit("ivanov", password));

        Assert.False(result.Success);
        Assert.Null(result.Connection);
        Assert.Equal(LogonError.InvalidCredentials, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, result.Message);
    }

    [Fact]
    public async Task Explicit_EmptyLogin_IsInvalidCredentials()
    {
        var result = await Logon(Service(), Explicit("  ", "demo"));

        Assert.Equal(LogonError.InvalidCredentials, result.Error);
        Assert.Contains("логин", result.Message);
    }

    [Theory]
    [InlineData("ivanov", "other.local")]
    [InlineData("ivanov", "corp.example")]
    [InlineData("OTHER\\ivanov", "corp.local")]
    [InlineData("ivanov@other.local", null)]
    [InlineData("ivanov@other.local", "corp.local")]
    public async Task Explicit_ForeignDomain_IsDnsFailure(string login, string? domain)
    {
        var result = await Logon(Service(), Explicit(login, "demo", domain));

        Assert.False(result.Success);
        Assert.Equal(LogonError.DnsFailure, result.Error);
        Assert.Contains("DNS", result.Message);
        Assert.DoesNotContain("demo", result.Message);
    }

    [Fact]
    public async Task Explicit_ForeignDomain_WinsOverBadLogin()
    {
        var result = await Logon(Service(), Explicit("locked", "demo", "other.local"));

        Assert.Equal(LogonError.DnsFailure, result.Error);
    }

    [Fact]
    public async Task Explicit_NoDomainOnStandaloneComputer_AsksForDomain()
    {
        var standalone = await Logon(Service(joined: false), Explicit("ivanov", "demo", domain: null));
        var joined = await Logon(Service(joined: true), Explicit("ivanov", "demo", domain: null));
        var standaloneWithUpn = await Logon(Service(joined: false), Explicit("ivanov@corp.local", "demo", domain: null));

        Assert.Equal(LogonError.DomainNotSpecified, standalone.Error);
        Assert.True(joined.Success);
        Assert.True(standaloneWithUpn.Success);
    }

    [Fact]
    public async Task Explicit_CustomDemoDomain_IsTheOnlyOneAccepted()
    {
        var service = new DemoDomainLogonService(domainName: "test.local", timings: DemoTimings.Instant);

        var ok = await Logon(service, Explicit("ivanov", "demo", "test.local"));
        var no = await Logon(service, Explicit("ivanov", "demo", "corp.local"));

        Assert.True(ok.Success);
        Assert.Equal("TEST\\ivanov", ok.Connection!.AccountName);
        Assert.Equal("DC01.test.local", ok.Connection.DomainController);
        Assert.Equal(LogonError.DnsFailure, no.Error);
    }

    // ---------- задержка, отмена, подключение ----------

    [Fact]
    public async Task Logon_TakesAboutFourHundredMilliseconds_ByDefault()
    {
        var service = new DemoDomainLogonService();

        var watch = Stopwatch.StartNew();
        var result = await service.LogonAsync(new LogonRequest(LogonMode.CurrentUser, null, null, null, null), CancellationToken.None);
        watch.Stop();

        Assert.True(result.Success);
        Assert.True(watch.ElapsedMilliseconds >= 370, $"Вход занял {watch.ElapsedMilliseconds} мс, ожидалось около 400.");
        Assert.Equal(TimeSpan.FromMilliseconds(400), DemoTimings.Default.LogonDelay);
    }

    [Fact]
    public async Task Logon_Cancelled_Throws()
    {
        var service = new DemoDomainLogonService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LogonAsync(Explicit("ivanov", "demo"), cts.Token));
    }

    [Fact]
    public async Task Logon_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Service().LogonAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Connection_RunsDelegatesAsIs_AndIsDisposable()
    {
        var result = await Logon(Service(), Explicit("ivanov", "demo"));
        var connection = (DemoDomainConnection)result.Connection!;
        var ran = 0;

        var value = connection.RunImpersonated(() => 40 + 2);
        connection.RunImpersonated(() => { ran++; });

        Assert.Equal(42, value);
        Assert.Equal(1, ran);
        Assert.Throws<InvalidOperationException>(() => connection.RunImpersonated<int>(() => throw new InvalidOperationException("из делегата")));
        Assert.Throws<ArgumentNullException>(() => connection.RunImpersonated((Action)null!));
        Assert.False(connection.IsDisposed);

        connection.Dispose();

        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task Connection_HidesPasswordInTextForms()
    {
        var result = await Logon(Service(), Explicit("ivanov", "demo"));

        Assert.DoesNotContain("demo", result.Connection!.Credential!.ToString());
        Assert.DoesNotContain("demo", result.Connection.AccountName);
    }
}
