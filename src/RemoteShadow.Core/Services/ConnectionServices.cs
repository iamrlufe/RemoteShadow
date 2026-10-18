using RemoteShadow.Core.Models;

namespace RemoteShadow.Core.Services;

/// <summary>
/// Подключение к домену от имени выбранной учётной записи. Все сетевые операции идут через него,
/// поэтому модули не знают, в каком режиме работает программа.
/// </summary>
public interface IDomainConnection : IDisposable
{
    /// <summary>DNS-имя домена: corp.local.</summary>
    string DomainName { get; }

    /// <summary>Контроллер домена, с которым работаем (FQDN), если известен.</summary>
    string? DomainController { get; }

    /// <summary>От чьего имени работаем: CORP\adm-ivanov.</summary>
    string AccountName { get; }

    /// <summary>false — текущий пользователь Windows (Kerberos), true — явные учётные данные.</summary>
    bool UsesExplicitCredentials { get; }

    /// <summary>«Kerberos», «NTLM» или «Negotiate» — для строки состояния.</summary>
    string AuthenticationKind { get; }

    /// <summary>Учётные данные для явного режима; null для текущего пользователя.</summary>
    AccountCredential? Credential { get; }

    /// <summary>
    /// Выполняет блокирующую сетевую операцию (WTS, CIM, SMB) от имени подключения.
    /// Для явных данных — внутри олицетворения LogonUser(LOGON32_LOGON_NEW_CREDENTIALS), аналог runas /netonly.
    /// Для текущего пользователя — как есть.
    /// </summary>
    T RunImpersonated<T>(Func<T> action);

    void RunImpersonated(Action action);
}

/// <summary>Текущее подключение к домену. Singleton: сервисы берут <see cref="Current"/> при каждом вызове.</summary>
public interface ICurrentConnection
{
    IDomainConnection? Current { get; }
    event EventHandler? Changed;

    /// <summary>Заменяет подключение. Предыдущее освобождается (Dispose).</summary>
    void Set(IDomainConnection? connection);

    /// <summary>Подключение или <see cref="OperationFailedException"/> с <see cref="OperationFailure.NotLoggedIn"/>.</summary>
    IDomainConnection Require();
}

public sealed class CurrentConnection : ICurrentConnection
{
    private readonly object _gate = new();
    private IDomainConnection? _current;

    public IDomainConnection? Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? Changed;

    public void Set(IDomainConnection? connection)
    {
        IDomainConnection? old;
        lock (_gate)
        {
            old = _current;
            _current = connection;
        }

        if (!ReferenceEquals(old, connection)) old?.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IDomainConnection Require() =>
        Current ?? throw new OperationFailedException("Не выполнен вход в домен.", OperationFailure.NotLoggedIn);
}

/// <summary>Вход в домен.</summary>
public interface IDomainLogonService
{
    /// <summary>Состоит ли компьютер в домене и кто залогинен в Windows (NetGetJoinInformation).</summary>
    DomainEnvironmentInfo DetectEnvironment();

    /// <summary>
    /// Проверяет данные пробным LDAP-подключением и возвращает подключение либо понятную ошибку.
    /// Не бросает исключения для ожидаемых ошибок (неверный пароль и т. п.) — они в <see cref="LogonResult"/>.
    /// </summary>
    Task<LogonResult> LogonAsync(LogonRequest request, CancellationToken ct);
}

/// <summary>Хранилище входа: Диспетчер учётных данных Windows. Пароль сохраняется только по явному решению админа.</summary>
public interface ICredentialStore
{
    StoredLogon? Load();
    void Save(StoredLogon logon);
    void Delete();
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
