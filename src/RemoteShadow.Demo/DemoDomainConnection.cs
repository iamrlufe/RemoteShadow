using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>Тестовое подключение к домену: выполняет делегаты как есть, ничего не олицетворяет и ни с чем не соединяется.</summary>
public sealed class DemoDomainConnection : IDomainConnection
{
    public DemoDomainConnection(
        string domainName,
        string? domainController,
        string accountName,
        bool usesExplicitCredentials,
        AccountCredential? credential = null,
        string authenticationKind = "Kerberos (демо)")
    {
        DomainName = domainName;
        DomainController = domainController;
        AccountName = accountName;
        UsesExplicitCredentials = usesExplicitCredentials;
        Credential = credential;
        AuthenticationKind = authenticationKind;
    }

    public string DomainName { get; }
    public string? DomainController { get; }
    public string AccountName { get; }
    public bool UsesExplicitCredentials { get; }
    public string AuthenticationKind { get; }
    public AccountCredential? Credential { get; }

    /// <summary>true после <see cref="Dispose"/>. Работе это не мешает: демо-подключение ресурсов не держит.</summary>
    public bool IsDisposed { get; private set; }

    public T RunImpersonated<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }

    public void RunImpersonated(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public void Dispose() => IsDisposed = true;
}
