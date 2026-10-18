using System.Diagnostics;
using System.Text;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace ShadowSpike;

/// <summary>Настройки в памяти: WtsSessionService читает их при каждом вызове, на диск ничего не пишется.</summary>
internal sealed class MemorySettingsStore : ISettingsStore
{
    private AppSettings _settings = new();

    public AppSettings Load() => _settings;

    public void Save(AppSettings settings) => _settings = settings;
}

internal static class ConsoleIo
{
    /// <summary>Читает пароль без эха. null — ввод перенаправлен или пароль пуст.</summary>
    public static string? ReadPassword(string prompt)
    {
        if (Console.IsInputRedirected) return null;
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }

        Console.WriteLine();
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Вопрос да/нет. Пустой ответ и закрытый ввод — «нет».</summary>
    public static bool AskYesNo(string question)
    {
        Console.Write(question + " ");
        var answer = Console.IsInputRedirected ? null : Console.ReadLine();
        if (answer is null)
        {
            Console.WriteLine("(нет ответа, считаем: нет)");
            return false;
        }

        var a = answer.Trim().ToLowerInvariant();
        return a is "y" or "yes" or "д" or "да";
    }
}

internal sealed record ToolOutput(int ExitCode, string Output, bool TimedOut);

internal static class ProcessTool
{
    /// <summary>Запускает программу без оболочки (cmd /c не используется) и собирает вывод.</summary>
    public static async Task<ToolOutput> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = OemEncoding(),
            StandardErrorEncoding = OemEncoding(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* уже завершился */ }
            return new ToolOutput(-1, string.Empty, true);
        }

        return new ToolOutput(process.ExitCode, await stdout + await stderr, false);
    }

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }
}
