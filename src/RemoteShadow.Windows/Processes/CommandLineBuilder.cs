using System.Text;

namespace RemoteShadow.Windows;

/// <summary>
/// Собирает командную строку для CreateProcessWithLogonW по правилам разбора CommandLineToArgvW / CRT:
/// пробелы и табуляции требуют кавычек, кавычка внутри аргумента экранируется обратной косой, а обратные косые
/// перед кавычкой (в том числе закрывающей) удваиваются. Это нужно только там, где нет списка аргументов
/// (Process.Start принимает ArgumentList и собирает строку сам).
/// </summary>
internal static class CommandLineBuilder
{
    /// <summary>Путь к программе и аргументы одной строкой. Максимум для CreateProcessWithLogonW — 1024 символа.</summary>
    public const int MaxLength = 1024;

    public static string Build(string programPath, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        AppendProgram(sb, programPath);
        foreach (var argument in arguments)
        {
            sb.Append(' ');
            AppendArgument(sb, argument);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Первый токен (argv[0]) разбирается проще остальных: обратные косые в нём ничего не экранируют,
    /// кавычки нужны только из-за пробелов. Кавычка в пути недопустима.
    /// </summary>
    public static void AppendProgram(StringBuilder sb, string programPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programPath);
        if (programPath.Contains('"') || programPath.Contains('\0'))
            throw new ArgumentException("Недопустимые символы в пути к программе.", nameof(programPath));

        if (programPath.IndexOfAny([' ', '\t']) >= 0)
            sb.Append('"').Append(programPath).Append('"');
        else
            sb.Append(programPath);
    }

    /// <summary>Добавляет один аргумент так, чтобы CommandLineToArgvW вернул его без изменений.</summary>
    public static void AppendArgument(StringBuilder sb, string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0'))
            throw new ArgumentException("Аргумент содержит нулевой символ.", nameof(argument));

        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        for (var i = 0; ; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == argument.Length)
            {
                // Закрывающая кавычка: косые перед ней удваиваются, чтобы она не была экранирована.
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(argument[i]);
            }
        }

        sb.Append('"');
    }
}
