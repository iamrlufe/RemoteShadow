using System.DirectoryServices.Protocols;
using RemoteShadow.Core.Services;

namespace RemoteShadow.Windows;

/// <summary>
/// Открытое подключение LDAP на время одной операции. Вызовы LdapConnection блокирующие и не отменяются,
/// поэтому они идут в Task.Run, а при отмене мы перестаём ждать. Подключение закрывается только после того,
/// как незавершённый вызов вернётся: закрытие посреди запроса другим потоком небезопасно.
/// </summary>
internal sealed class LdapSession : IDisposable
{
    private readonly LdapConnection _ldap;
    private Task? _pending;

    private LdapSession(LdapConnection ldap) => _ldap = ldap;

    public static async Task<LdapSession> OpenAsync(IDomainConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var open = Task.Run(() => LdapConnectionFactory.CreateBound(connection), CancellationToken.None);
        try
        {
            return new LdapSession(await open.WaitAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Подключение ещё открывается: закроем его, когда откроется.
            _ = open.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully) t.Result.Dispose();
                },
                TaskScheduler.Default);
            throw;
        }
    }

    public Task<T> RunAsync<T>(Func<LdapConnection, T> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var task = Task.Run(() => work(_ldap), CancellationToken.None);
        _pending = task;
        return task.WaitAsync(ct);
    }

    public void Dispose()
    {
        var pending = _pending;
        if (pending is null || pending.IsCompleted)
        {
            _ldap.Dispose();
            return;
        }

        _ = pending.ContinueWith(_ => _ldap.Dispose(), TaskScheduler.Default);
    }
}
