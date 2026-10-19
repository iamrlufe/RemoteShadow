using System.Security.Cryptography;
using System.Text;

namespace RemoteShadow.App.Shell;

/// <summary>
/// Один экземпляр программы на папку данных. Два экземпляра с общим <c>settings.json</c> перезаписывают правки друг
/// друга (каждый держит настройки в памяти и пишет файл целиком). Имя мьютекса зависит от папки данных, поэтому
/// демо-режим (временная папка) и рабочий режим друг другу не мешают.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>Возвращает защиту, если экземпляров для этой папки ещё нет; иначе null.</summary>
    public static SingleInstanceGuard? TryAcquire(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)).ToLowerInvariant())))[..16];
        var mutex = new Mutex(true, $@"Local\RemoteShadow-{key}", out var createdNew);
        if (createdNew) return new SingleInstanceGuard(mutex);

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null) return;

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Освобождает не тот поток, который захватил мьютекс: ниже Dispose закроет дескриптор, ОС освободит имя.
        }

        mutex.Dispose();
    }
}
