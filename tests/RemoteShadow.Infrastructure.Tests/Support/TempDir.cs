namespace RemoteShadow.Infrastructure.Tests;

/// <summary>Временный каталог на время теста.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = Directory.CreateTempSubdirectory("rs-infra-").FullName;
    }

    public string Path { get; }

    public AppPaths Paths => new(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Не критично: каталог во временной папке.
        }
    }
}
