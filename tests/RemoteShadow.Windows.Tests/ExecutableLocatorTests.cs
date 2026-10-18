using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class ExecutableLocatorTests
{
    private const string System32 = @"C:\Windows\System32";
    private const string Windows = @"C:\Windows";
    private const string Tools = @"C:\Tools";
    private const string WindowsApps = @"C:\Users\admin\AppData\Local\Microsoft\WindowsApps";

    private static readonly string[] Directories = [System32, Windows, Tools, WindowsApps];

    private static Func<string, bool> Exists(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void BareName_GetsExeAndIsFoundInDirectory()
    {
        var found = ExecutableLocator.Find("mstsc", Directories, Exists(@"C:\Windows\System32\mstsc.exe"));

        Assert.Equal(@"C:\Windows\System32\mstsc.exe", found);
    }

    [Fact]
    public void BareNameWithExtension_IsNotChanged()
    {
        var found = ExecutableLocator.Find("wt.exe", Directories, Exists(WindowsApps + @"\wt.exe"));

        Assert.Equal(WindowsApps + @"\wt.exe", found);
    }

    [Fact]
    public void System32_WinsOverLaterDirectories()
    {
        var found = ExecutableLocator.Find(
            "ssh",
            Directories,
            Exists(@"C:\Tools\ssh.exe", @"C:\Windows\System32\ssh.exe", WindowsApps + @"\ssh.exe"));

        Assert.Equal(@"C:\Windows\System32\ssh.exe", found);
    }

    [Fact]
    public void FallsThroughToLaterDirectories()
    {
        var found = ExecutableLocator.Find("ssh", Directories, Exists(@"C:\Tools\ssh.exe"));

        Assert.Equal(@"C:\Tools\ssh.exe", found);
    }

    [Fact]
    public void NotFound_IsNull()
    {
        Assert.Null(ExecutableLocator.Find("nothing", Directories, Exists()));
    }

    [Fact]
    public void BlankDirectoriesAreSkipped()
    {
        var found = ExecutableLocator.Find("x", ["", "  ", Tools], Exists(@"C:\Tools\x.exe"));

        Assert.Equal(@"C:\Tools\x.exe", found);
    }

    [Fact]
    public void FullPath_ExistingFile_IsReturned()
    {
        var found = ExecutableLocator.Find(@"C:\Tools\ssh.exe", Directories, Exists(@"C:\Tools\ssh.exe"));

        Assert.Equal(@"C:\Tools\ssh.exe", found);
    }

    [Fact]
    public void FullPath_WithoutExtension_GetsExe()
    {
        var found = ExecutableLocator.Find(@"C:\Tools\ssh", Directories, Exists(@"C:\Tools\ssh.exe"));

        Assert.Equal(@"C:\Tools\ssh.exe", found);
    }

    [Fact]
    public void FullPath_MissingFile_IsNotSubstitutedFromOtherDirectories()
    {
        var found = ExecutableLocator.Find(@"C:\Evil\ssh.exe", Directories, Exists(@"C:\Windows\System32\ssh.exe"));

        Assert.Null(found);
    }

    [Fact]
    public void FullPath_QuotedByUser_IsUnquoted()
    {
        var found = ExecutableLocator.Find("\"C:\\Program Files\\App\\app.exe\"", Directories, Exists(@"C:\Program Files\App\app.exe"));

        Assert.Equal(@"C:\Program Files\App\app.exe", found);
    }

    [Fact]
    public void UncPath_IsAcceptedWhenFileExists()
    {
        var found = ExecutableLocator.Find(@"\\srv\tools\x.exe", Directories, Exists(@"\\srv\tools\x.exe"));

        Assert.Equal(@"\\srv\tools\x.exe", found);
    }

    [Theory]
    [InlineData(@"sub\x.exe")]
    [InlineData(@"..\x.exe")]
    [InlineData("sub/x.exe")]
    [InlineData(@".\x.exe")]
    [InlineData("C:x.exe")]
    [InlineData(@"\x.exe")]
    public void RelativePathsAreRejected_EvenIfSuchFileWouldExist(string name)
    {
        // Результат не должен зависеть от текущего каталога процесса.
        var found = ExecutableLocator.Find(name, Directories, _ => true);

        Assert.Null(found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("a\0b")]
    [InlineData("a|b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    public void InvalidNames_AreNull(string? name)
    {
        Assert.Null(ExecutableLocator.Find(name, Directories, _ => true));
    }

    [Theory]
    [InlineData("mstsc", "mstsc.exe")]
    [InlineData("mstsc.exe", "mstsc.exe")]
    [InlineData("wt", "wt.exe")]
    [InlineData("tool.cmd", "tool.cmd")]
    [InlineData("ssh-keygen", "ssh-keygen.exe")]
    public void WithDefaultExtension(string name, string expected)
    {
        Assert.Equal(expected, ExecutableLocator.WithDefaultExtension(name));
    }

    [Fact]
    public void DefaultSearchDirectories_AreAbsoluteUniqueAndStartWithSystem32()
    {
        var directories = ExecutableLocator.DefaultSearchDirectories();

        Assert.NotEmpty(directories);
        Assert.Equal(Environment.SystemDirectory, directories[0], ignoreCase: true);
        Assert.All(directories, d => Assert.True(Path.IsPathFullyQualified(d), d));
        Assert.Equal(directories.Count, directories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DefaultSearchDirectories_IncludeWindowsAppsExactlyOnce()
    {
        // wt.exe лежит только там; если каталог уже есть в PATH, второй раз он не добавляется.
        var directories = ExecutableLocator.DefaultSearchDirectories();

        var apps = directories.Where(d => d.EndsWith(@"Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Single(apps);
    }

    [Fact]
    public void RealSystem_FindsCmd()
    {
        var found = ExecutableLocator.Find("cmd", ExecutableLocator.DefaultSearchDirectories(), File.Exists);

        Assert.NotNull(found);
        Assert.True(File.Exists(found));
        Assert.StartsWith(Environment.SystemDirectory, found, StringComparison.OrdinalIgnoreCase);
    }
}
