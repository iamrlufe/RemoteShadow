using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

/// <summary>Проверки без сети и без домена: регистрация сервисов, быстрые отказы до обращения к LDAP, настройки подключения.</summary>
public class WindowsServiceRegistrationTests
{
    [Fact]
    public void AddRemoteShadowWindows_RegistersCoreServicesAsSingletons()
    {
        var services = new ServiceCollection();

        services.AddRemoteShadowWindows();

        void Expect<TService, TImplementation>()
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TService));
            Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        Expect<IDomainLogonService, WindowsDomainLogonService>();
        Expect<IComputerDirectory, LdapComputerDirectory>();
        Expect<IUserDirectory, LdapUserDirectory>();
        Expect<IProcessLauncher, WindowsProcessLauncher>();
        Expect<ICredentialStore, CredentialManagerStore>();
    }

    [Fact]
    public void AddRemoteShadowWindows_DoesNotRegisterCurrentConnection()
    {
        var services = new ServiceCollection();

        services.AddRemoteShadowWindows();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ICurrentConnection));
    }

    [Fact]
    public void AddRemoteShadowWindows_ReturnsSameCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddRemoteShadowWindows());
    }
}

public class WithoutConnectionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static void AssertNotLoggedIn(OperationFailedException ex) =>
        Assert.Equal(OperationFailure.NotLoggedIn, ex.Failure);

    [Fact]
    public async Task ComputerDirectory_GetRootDn_RequiresLogon()
    {
        var directory = new LdapComputerDirectory(new CurrentConnection());

        AssertNotLoggedIn(await Assert.ThrowsAsync<OperationFailedException>(() => directory.GetRootDnAsync(Ct)));
    }

    [Fact]
    public async Task ComputerDirectory_Enumerate_RequiresLogon()
    {
        var directory = new LdapComputerDirectory(new CurrentConnection());

        var ex = await Assert.ThrowsAsync<OperationFailedException>(async () =>
        {
            await foreach (var _ in directory.EnumerateAsync(new ComputerQuery(), Ct)) { }
        });

        AssertNotLoggedIn(ex);
    }

    [Fact]
    public async Task ComputerDirectory_OuChildren_RequiresLogon()
    {
        var directory = new LdapComputerDirectory(new CurrentConnection());

        AssertNotLoggedIn(await Assert.ThrowsAsync<OperationFailedException>(() => directory.GetOuChildrenAsync("DC=corp,DC=local", Ct)));
    }

    [Fact]
    public void ComputerDirectory_DomainController_IsNullWithoutConnection()
    {
        Assert.Null(new LdapComputerDirectory(new CurrentConnection()).DomainController);
    }

    [Fact]
    public async Task UserDirectory_Search_RequiresLogon()
    {
        var directory = new LdapUserDirectory(new CurrentConnection());

        AssertNotLoggedIn(await Assert.ThrowsAsync<OperationFailedException>(() => directory.SearchAsync("иванов", 10, Ct)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UserDirectory_BlankSearch_IsEmptyWithoutAnyRequest(string text)
    {
        var directory = new LdapUserDirectory(new CurrentConnection());

        Assert.Empty(await directory.SearchAsync(text, 10, Ct));
        Assert.Empty(await directory.SearchGroupsAsync(text, 10, Ct));
        Assert.Null(await directory.GetBySamAccountNameAsync(text, Ct));
    }

    [Fact]
    public async Task UserDirectory_ZeroMax_IsEmptyWithoutAnyRequest()
    {
        var directory = new LdapUserDirectory(new CurrentConnection());

        Assert.Empty(await directory.SearchAsync("иванов", 0, Ct));
        Assert.Empty(await directory.SearchGroupsAsync("vpn", 0, Ct));
    }

    [Fact]
    public async Task UserDirectory_EmptyPassword_IsInvalidInputBeforeAnyRequest()
    {
        var directory = new LdapUserDirectory(new CurrentConnection());

        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => directory.ResetPasswordAsync("CN=A,DC=corp,DC=local", "", false, Ct));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
    }

    [Fact]
    public async Task UserDirectory_EmptyDn_IsInvalidInput()
    {
        var directory = new LdapUserDirectory(new CurrentConnection());

        foreach (var action in new Func<Task>[]
                 {
                     () => directory.ResetPasswordAsync(" ", "P@ssw0rd", false, Ct),
                     () => directory.UnlockAsync("", Ct),
                     () => directory.SetEnabledAsync("", true, Ct),
                     () => directory.GetGroupsAsync("", Ct),
                     () => directory.AddToGroupAsync("", "CN=G,DC=corp,DC=local", Ct),
                     () => directory.AddToGroupAsync("CN=A,DC=corp,DC=local", "", Ct),
                     () => directory.RemoveFromGroupAsync("CN=A,DC=corp,DC=local", " ", Ct),
                 })
        {
            var ex = await Assert.ThrowsAsync<OperationFailedException>(action);
            Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
        }
    }

    [Fact]
    public async Task UserDirectory_Changes_RequireLogon()
    {
        var directory = new LdapUserDirectory(new CurrentConnection());
        const string user = "CN=A,DC=corp,DC=local";

        foreach (var action in new Func<Task>[]
                 {
                     () => directory.ResetPasswordAsync(user, "P@ssw0rd", true, Ct),
                     () => directory.UnlockAsync(user, Ct),
                     () => directory.SetEnabledAsync(user, false, Ct),
                     () => directory.GetGroupsAsync(user, Ct),
                     () => directory.AddToGroupAsync(user, "CN=G,DC=corp,DC=local", Ct),
                     () => directory.RemoveFromGroupAsync(user, "CN=G,DC=corp,DC=local", Ct),
                 })
        {
            AssertNotLoggedIn(await Assert.ThrowsAsync<OperationFailedException>(action));
        }
    }

    [Fact]
    public async Task Logon_Explicit_WithoutLoginOrPassword_FailsBeforeAnyNetworkCall()
    {
        var service = new WindowsDomainLogonService();

        var noLogin = await service.LogonAsync(new LogonRequest(LogonMode.Explicit, "corp.local", null, " ", "pwd"), Ct);
        var noPassword = await service.LogonAsync(new LogonRequest(LogonMode.Explicit, "corp.local", null, "ivanov", ""), Ct);

        Assert.False(noLogin.Success);
        Assert.Equal(LogonError.InvalidCredentials, noLogin.Error);
        Assert.Contains("логин", noLogin.Message);
        Assert.False(noPassword.Success);
        Assert.Equal(LogonError.InvalidCredentials, noPassword.Error);
        Assert.Contains("пароль", noPassword.Message);
        Assert.Null(noLogin.Connection);
    }

    [Fact]
    public void DetectEnvironment_ReportsMachineAndUser()
    {
        var info = new WindowsDomainLogonService().DetectEnvironment();

        Assert.Equal(Environment.MachineName, info.ComputerName);
        Assert.False(string.IsNullOrWhiteSpace(info.CurrentUser));
        if (!info.IsDomainJoined) Assert.Null(info.DomainName);
    }
}

public class ProcessLauncherTests
{
    private static WindowsProcessLauncher Create() => new(new CurrentConnection());

    [Fact]
    public void FindExecutable_FindsSystemProgram()
    {
        var path = Create().FindExecutable("cmd");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith("cmd.exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindExecutable_UnknownProgram_IsNull()
    {
        Assert.Null(Create().FindExecutable("remoteshadow-no-such-program-7f3a"));
    }

    [Fact]
    public async Task StartAsync_MissingProgram_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => Create().StartAsync("remoteshadow-no-such-program-7f3a", ["/x"], CancellationToken.None));

        Assert.Equal(OperationFailure.NotFound, ex.Failure);
        Assert.Contains("remoteshadow-no-such-program-7f3a", ex.Message);
    }

    [Fact]
    public async Task StartAsync_NullCharacterInArgument_IsInvalidInput_AndNothingIsStarted()
    {
        var ex = await Assert.ThrowsAsync<OperationFailedException>(
            () => Create().StartAsync("cmd.exe", ["/c", "echo a\0b"], CancellationToken.None));

        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
    }

    [Fact]
    public async Task StartAsync_AlreadyCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create().StartAsync("cmd.exe", ["/c", "exit"], cts.Token));
    }
}

public class WindowsDomainConnectionTests
{
    [Fact]
    public void CurrentUser_RunsActionsAsIs()
    {
        using var connection = new WindowsDomainConnection("corp.local", "dc01.corp.local", @"CORP\ivanov", null);

        var value = connection.RunImpersonated(() => 42);
        var ran = false;
        connection.RunImpersonated(() => ran = true);

        Assert.Equal(42, value);
        Assert.True(ran);
        Assert.False(connection.UsesExplicitCredentials);
        Assert.Null(connection.Credential);
        Assert.Equal("corp.local", connection.DomainName);
        Assert.Equal("dc01.corp.local", connection.DomainController);
        Assert.Equal(@"CORP\ivanov", connection.AccountName);
    }

    [Fact]
    public void ExplicitCredentials_AreKeptOnlyInCredential()
    {
        var credential = new AccountCredential("CORP", "adm-ivanov", "S3cret!");
        using var connection = new WindowsDomainConnection("corp.local", "dc01.corp.local", @"CORP\adm-ivanov", credential);

        Assert.True(connection.UsesExplicitCredentials);
        Assert.Same(credential, connection.Credential);
        Assert.DoesNotContain("S3cret!", connection.AccountName);
        Assert.DoesNotContain("S3cret!", connection.AuthenticationKind);
        Assert.DoesNotContain("S3cret!", connection.ToString());
    }

    [Theory]
    [InlineData("dc01.corp.local", "CORP", "adm", "Kerberos")]
    [InlineData("10.0.0.5", "CORP", "adm", "NTLM")]
    [InlineData("dc01", "CORP", "adm", "NTLM")]
    [InlineData("dc01.corp.local", "", "adm@corp.local", "Kerberos")]
    [InlineData("dc01.corp.local", "", "adm", "NTLM")]
    [InlineData(null, "CORP", "adm", "Negotiate")]
    public void AuthenticationKind_IsAHeuristic(string? controller, string domain, string user, string expected)
    {
        using var connection = new WindowsDomainConnection(
            "corp.local", controller, @"CORP\adm", new AccountCredential(domain, user, "p"));

        Assert.Equal(expected, connection.AuthenticationKind);
    }

    [Fact]
    public void CurrentUser_AuthenticationKind_IsKerberosForFqdn()
    {
        using var connection = new WindowsDomainConnection("corp.local", "dc01.corp.local", @"CORP\ivanov", null);

        Assert.Equal("Kerberos", connection.AuthenticationKind);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndExplicitImpersonationAfterDisposeThrows()
    {
        var connection = new WindowsDomainConnection(
            "corp.local", "dc01.corp.local", @"CORP\adm", new AccountCredential("CORP", "adm", "p"));

        connection.Dispose();
        connection.Dispose();

        Assert.Throws<ObjectDisposedException>(() => connection.RunImpersonated(() => 1));
    }

    [Fact]
    public void NullAction_Throws()
    {
        using var connection = new WindowsDomainConnection("corp.local", null, @"CORP\ivanov", null);

        Assert.Throws<ArgumentNullException>(() => connection.RunImpersonated((Action)null!));
        Assert.Throws<ArgumentNullException>(() => connection.RunImpersonated((Func<int>)null!));
    }
}

public class LdapConnectionFactoryTests
{
    [Fact]
    public void BuildNetworkCredential_SplitsDomainAndLogin()
    {
        var network = LdapConnectionFactory.BuildNetworkCredential(new AccountCredential("CORP", "ivanov", "p"));

        Assert.Equal("ivanov", network.UserName);
        Assert.Equal("CORP", network.Domain);
        Assert.Equal("p", network.Password);
    }

    [Fact]
    public void BuildNetworkCredential_Upn_HasNoDomain()
    {
        var network = LdapConnectionFactory.BuildNetworkCredential(new AccountCredential("", "ivanov@corp.local", "p"));

        Assert.Equal("ivanov@corp.local", network.UserName);
        Assert.Equal(string.Empty, network.Domain);
    }

    [Fact]
    public void BuildNetworkCredential_UpnWinsOverDomain()
    {
        var network = LdapConnectionFactory.BuildNetworkCredential(new AccountCredential("CORP", "ivanov@corp.local", "p"));

        Assert.Equal(string.Empty, network.Domain);
    }

    [Fact]
    public void Create_AppliesSigningSealingAndProtocolSettingsWithoutConnecting()
    {
        using var ldap = LdapConnectionFactory.Create("dc01.corp.local", new NetworkCredential("u", "p", "CORP"), TimeSpan.FromSeconds(7));

        Assert.Equal(AuthType.Negotiate, ldap.AuthType);
        Assert.Equal(3, ldap.SessionOptions.ProtocolVersion);
        Assert.True(ldap.SessionOptions.Signing);
        Assert.True(ldap.SessionOptions.Sealing);
        Assert.Equal(ReferralChasingOptions.None, ldap.SessionOptions.ReferralChasing);
        Assert.Equal(TimeSpan.FromSeconds(7), ldap.Timeout);
        Assert.False(ldap.SessionOptions.SecureSocketLayer);
    }

    [Fact]
    public void Create_ForCurrentUser_AcceptsNullCredential()
    {
        using var ldap = LdapConnectionFactory.Create("dc01.corp.local", null, TimeSpan.FromSeconds(5));

        Assert.True(ldap.SessionOptions.Sealing);
    }
}

public class PagedSearchTests
{
    [Fact]
    public void Constructor_AddsPageControlWithRequestedSize()
    {
        var request = new SearchRequest("DC=corp,DC=local", "(objectClass=*)", SearchScope.Subtree);

        var search = new PagedSearch(request, 500);

        var control = Assert.Single(request.Controls.OfType<PageResultRequestControl>());
        Assert.Equal(500, control.PageSize);
        Assert.False(search.Finished);
    }
}
