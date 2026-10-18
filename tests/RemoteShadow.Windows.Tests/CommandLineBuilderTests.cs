using System.Runtime.InteropServices;
using System.Text;
using RemoteShadow.Windows;

namespace RemoteShadow.Windows.Tests;

public class CommandLineBuilderTests
{
    private const string Program = @"C:\Windows\System32\mstsc.exe";

    [Fact]
    public void NoArguments_IsJustProgram()
    {
        Assert.Equal(Program, CommandLineBuilder.Build(Program, Array.Empty<string>()));
    }

    [Fact]
    public void SimpleArguments_AreNotQuoted()
    {
        Assert.Equal(
            $"{Program} /v:pc-acc-014.corp.local /shadow:2 /control /noConsentPrompt",
            CommandLineBuilder.Build(Program, ["/v:pc-acc-014.corp.local", "/shadow:2", "/control", "/noConsentPrompt"]));
    }

    [Theory]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("new\nline", "\"new\nline\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("\"", "\"\\\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")] // a\"b → косая удваивается, плюс экранирование кавычки
    [InlineData("C:\\Program Files\\", "\"C:\\Program Files\\\\\"")] // косая в конце перед закрывающей кавычкой
    [InlineData("C:\\dir\\file", "C:\\dir\\file")] // косые не перед кавычкой не трогаем
    [InlineData("\\\\server\\share", "\\\\server\\share")]
    [InlineData("\\\\server\\my share", "\"\\\\server\\my share\"")]
    [InlineData("end\\\\ space\\\\", "\"end\\\\ space\\\\\\\\\"")]
    public void ArgumentQuoting(string argument, string expected)
    {
        var sb = new StringBuilder();

        CommandLineBuilder.AppendArgument(sb, argument);

        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void ProgramPathWithSpaces_IsQuotedWithoutEscapingBackslashes()
    {
        var line = CommandLineBuilder.Build(@"C:\Program Files\OpenSSH\ssh.exe", ["user@host"]);

        Assert.Equal("\"C:\\Program Files\\OpenSSH\\ssh.exe\" user@host", line);
    }

    [Fact]
    public void ProgramPathWithQuote_Throws()
    {
        Assert.Throws<ArgumentException>(() => CommandLineBuilder.Build("C:\\a\"b.exe", Array.Empty<string>()));
    }

    [Fact]
    public void EmptyProgram_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => CommandLineBuilder.Build(" ", Array.Empty<string>()));
    }

    [Fact]
    public void NullCharacterInArgument_Throws()
    {
        Assert.Throws<ArgumentException>(() => CommandLineBuilder.Build(Program, ["a\0b"]));
    }

    [Fact]
    public void ShellMetacharacters_AreJustText()
    {
        // Командная строка уходит прямо в CreateProcess, без cmd, поэтому &, |, % не интерпретируются; кавычки не нужны.
        Assert.Equal($"{Program} host&calc|x%PATH%", CommandLineBuilder.Build(Program, ["host&calc|x%PATH%"]));
    }

    public static IEnumerable<object[]> RoundTripCases() =>
    [
        [new[] { "/v:host", "/shadow:1", "/control" }],
        [new[] { "a b", "c  d", "" }],
        [new[] { "say \"hi\"", "\"", "\"\"", "a\"b" }],
        [new[] { "a\\\"b", "\\\"", "\\\\\"", "x\\\\\\\"y" }],
        [new[] { "C:\\Program Files\\", "C:\\Program Files\\\\", "C:\\x y\\z\\" }],
        [new[] { "\\\\server\\my share\\dir\\", "\\\\server\\share" }],
        [new[] { "tab\there", "new\nline", "vt\vx" }],
        [new[] { "-o", "StrictHostKeyChecking=no", "user name@host", "-p", "22" }],
        [new[] { "Иванов Иван", "путь с пробелом\\" }],
        [new[] { "trailing\\", "\\leading", "mid\\dle" }],
    ];

    /// <summary>Главная проверка: настоящий разбор Windows (CommandLineToArgvW) возвращает исходные аргументы.</summary>
    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip_ThroughCommandLineToArgvW(string[] arguments)
    {
        const string program = @"C:\Program Files\Some Tool\tool.exe";

        var line = CommandLineBuilder.Build(program, arguments);
        var parsed = NativeArgv.Parse(line);

        Assert.Equal(new[] { program }.Concat(arguments), parsed);
    }
}

internal static class NativeArgv
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    public static string[] Parse(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new InvalidOperationException("CommandLineToArgvW failed: " + Marshal.GetLastWin32Error());

        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }
}
