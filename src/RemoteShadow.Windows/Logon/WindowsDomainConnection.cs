using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Windows.Native;

namespace RemoteShadow.Windows;

/// <summary>
/// Подключение к домену. Для текущего пользователя сетевые операции выполняются как есть. Для явных учётных данных
/// блокирующие операции (WTS, CIM, SMB) идут внутри олицетворения токена LogonUser(LOGON32_LOGON_NEW_CREDENTIALS):
/// локально процесс остаётся самим собой, а в сеть выходит под указанной учётной записью (аналог runas /netonly).
/// Пароль хранится только в <see cref="Credential"/>.
/// </summary>
public sealed class WindowsDomainConnection : IDomainConnection
{
    private readonly object _tokenGate = new();
    private SafeAccessTokenHandle? _token;
    private bool _disposed;

    /// <param name="domainName">DNS-имя домена.</param>
    /// <param name="domainController">Контроллер домена или null, если не известен.</param>
    /// <param name="accountName">CORP\ivanov — для показа.</param>
    /// <param name="credential">Явные учётные данные; null — текущий пользователь Windows.</param>
    public WindowsDomainConnection(string domainName, string? domainController, string accountName, AccountCredential? credential)
    {
        DomainName = domainName;
        DomainController = domainController;
        AccountName = accountName;
        Credential = credential;
        AuthenticationKind = AuthenticationKinds.Resolve(domainController, IsDomainAccount(credential));
    }

    public string DomainName { get; }
    public string? DomainController { get; }
    public string AccountName { get; }
    public bool UsesExplicitCredentials => Credential is not null;
    public string AuthenticationKind { get; }
    public AccountCredential? Credential { get; }

    public T RunImpersonated<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Credential is null ? action() : WindowsIdentity.RunImpersonated(GetToken(), action);
    }

    public void RunImpersonated(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Credential is null)
        {
            action();
            return;
        }

        WindowsIdentity.RunImpersonated(GetToken(), action);
    }

    public void Dispose()
    {
        lock (_tokenGate)
        {
            _disposed = true;
            _token?.Dispose();
            _token = null;
        }
    }

    /// <summary>Доменная учётная запись: есть домен или UPN. Для текущего пользователя тоже true.</summary>
    private static bool IsDomainAccount(AccountCredential? credential) =>
        credential is null || !string.IsNullOrEmpty(credential.Domain) || credential.UserName.Contains('@');

    /// <summary>Токен создаётся один раз при первом обращении и живёт до Dispose.</summary>
    private SafeAccessTokenHandle GetToken()
    {
        lock (_tokenGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_token is { IsInvalid: false }) return _token;

            var credential = Credential!;
            var domain = credential.UserName.Contains('@') || string.IsNullOrEmpty(credential.Domain)
                ? null
                : credential.Domain;

            if (!NativeMethods.LogonUser(
                    credential.UserName,
                    domain,
                    credential.Password,
                    NativeMethods.Logon32LogonNewCredentials,
                    NativeMethods.Logon32ProviderWinNT50,
                    out var token))
            {
                var code = Marshal.GetLastPInvokeError();
                token.Dispose();
                throw new OperationFailedException(
                    $"Не удалось подготовить учётные данные для сетевых операций: {new Win32Exception(code).Message} (код {code}).",
                    OperationFailure.Other);
            }

            _token = token;
            return token;
        }
    }
}
