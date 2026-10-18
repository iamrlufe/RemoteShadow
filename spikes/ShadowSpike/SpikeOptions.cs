using System.Globalization;

namespace ShadowSpike;

/// <summary>Параметры командной строки. Пароль сюда не попадает никогда: он берётся из консоли или SPIKE_PASSWORD.</summary>
internal sealed record SpikeOptions
{
    public string? Domain { get; init; }
    public string? DomainController { get; init; }
    public string? UserName { get; init; }
    public bool CurrentUser { get; init; }
    public string? Host { get; init; }
    public string? OfflineHost { get; init; }
    public string? ClosedPortHost { get; init; }
    public int? ShadowSession { get; init; }
    public bool Rdp { get; init; }
    public bool NoConsent { get; init; }
    public string? ReportPath { get; init; }
    public string FindUser { get; init; } = "a";

    /// <summary>Путь отчёта: заданный или spike-report-&lt;дата&gt;.md в текущей папке.</summary>
    public string ResolveReportPath(DateTime now) =>
        string.IsNullOrWhiteSpace(ReportPath)
            ? $"spike-report-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md"
            : ReportPath!;
}

internal sealed record ParseResult(SpikeOptions? Options, string? Error, bool ShowHelp)
{
    public bool Ok => Options is not null && Error is null && !ShowHelp;
}

/// <summary>Разбор аргументов. Чистая функция: ничего не читает из окружения.</summary>
internal static class ArgParser
{
    public const string Usage =
        """
        Использование:
          ShadowSpike --domain corp.local [--dc dc01.corp.local] [--user adm-ivanov | --current-user]
                      [--host pc-acc-014.corp.local] [--offline-host имя] [--closed-port-host имя]
                      [--shadow-session <номер>] [--rdp] [--no-consent]
                      [--find-user строка] [--report spike-report.md]

        Пароль запрашивается в консоли без эха или берётся из переменной окружения SPIKE_PASSWORD.
        В аргументах пароль не передаётся.
        """;

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--current-user", "--rdp", "--no-consent", "--help", "-h", "-?",
    };

    private static readonly HashSet<string> WithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--domain", "--dc", "--user", "--host", "--offline-host", "--closed-port-host",
        "--shadow-session", "--report", "--find-user",
    };

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inline = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                inline = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            if (Flags.Contains(arg))
            {
                if (inline is not null) return Fail($"Параметр {arg} не принимает значение.");
                flags.Add(arg);
            }
            else if (WithValue.Contains(arg))
            {
                string? value = inline;
                if (value is null)
                {
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Fail($"Для параметра {arg} нужно значение.");
                    value = args[++i];
                }

                if (string.IsNullOrWhiteSpace(value)) return Fail($"Пустое значение параметра {arg}.");
                values[arg] = value.Trim();
            }
            else if (arg.Equals("--password", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Пароль в аргументах не принимается. Введите его в консоли или задайте переменную SPIKE_PASSWORD.");
            }
            else
            {
                return Fail($"Неизвестный параметр: {arg}");
            }
        }

        if (flags.Contains("--help") || flags.Contains("-h") || flags.Contains("-?"))
            return new ParseResult(null, null, true);

        int? shadow = null;
        if (values.TryGetValue("--shadow-session", out var sid))
        {
            if (!int.TryParse(sid, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return Fail("--shadow-session: нужен неотрицательный номер сессии.");
            shadow = n;
        }

        var currentUser = flags.Contains("--current-user");
        values.TryGetValue("--user", out var user);
        if (currentUser && user is not null) return Fail("Параметры --user и --current-user взаимоисключающие.");

        var options = new SpikeOptions
        {
            Domain = Get(values, "--domain"),
            DomainController = Get(values, "--dc"),
            UserName = user,
            CurrentUser = currentUser,
            Host = Get(values, "--host"),
            OfflineHost = Get(values, "--offline-host"),
            ClosedPortHost = Get(values, "--closed-port-host"),
            ShadowSession = shadow,
            Rdp = flags.Contains("--rdp"),
            NoConsent = flags.Contains("--no-consent"),
            ReportPath = Get(values, "--report"),
            FindUser = Get(values, "--find-user") ?? "a",
        };

        if ((options.ShadowSession is not null || options.Rdp) && options.Host is null)
            return Fail("Для --shadow-session и --rdp нужен --host.");
        if (options.NoConsent && options.ShadowSession is null)
            return Fail("--no-consent имеет смысл только вместе с --shadow-session.");

        return new ParseResult(options, null, false);
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;

    private static ParseResult Fail(string error) => new(null, error, false);
}
