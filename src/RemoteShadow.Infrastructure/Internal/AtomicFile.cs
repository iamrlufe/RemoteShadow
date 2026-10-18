namespace RemoteShadow.Infrastructure;

/// <summary>Запись файла целиком: временный файл рядом и замена, чтобы при сбое остался либо старый файл, либо новый.</summary>
internal static class AtomicFile
{
    private const int Attempts = 4;

    public static void WriteAllBytes(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            MoveWithRetry(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // Антивирус или индексатор могут на мгновение держать целевой файл — повторяем несколько раз.
    private static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < Attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Временный файл не критичен.
        }
    }
}
