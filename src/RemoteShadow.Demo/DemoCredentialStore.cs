using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Demo;

/// <summary>Хранилище входа в памяти: после закрытия программы ничего не остаётся. Диспетчер учётных данных Windows не трогает.</summary>
public sealed class DemoCredentialStore : ICredentialStore
{
    private readonly object _gate = new();
    private StoredLogon? _logon;

    public StoredLogon? Load()
    {
        lock (_gate) return _logon;
    }

    public void Save(StoredLogon logon)
    {
        ArgumentNullException.ThrowIfNull(logon);
        lock (_gate) _logon = logon;
    }

    public void Delete()
    {
        lock (_gate) _logon = null;
    }
}
