using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Actions.Tests;

public sealed class FakeDomainConnection : IDomainConnection
{
    public FakeDomainConnection(string accountName = @"CORP\adm-ivanov") => AccountName = accountName;

    public string DomainName => "corp.local";
    public string? DomainController => "dc01.corp.local";
    public string AccountName { get; }
    public bool UsesExplicitCredentials => false;
    public string AuthenticationKind => "Kerberos";
    public AccountCredential? Credential => null;

    public T RunImpersonated<T>(Func<T> action) => action();

    public void RunImpersonated(Action action) => action();

    public void Dispose()
    {
    }
}
