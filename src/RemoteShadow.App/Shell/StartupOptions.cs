namespace RemoteShadow.App.Shell;

/// <summary>
/// Параметры запуска.
/// <list type="bullet">
/// <item><c>--demo</c> (или переменная <c>REMOTESHADOW_DEMO=1</c>) — демо-режим без домена.</item>
/// <item><c>--login</c> — не входить тихо под текущим пользователем Windows, а показать окно входа.</item>
/// <item><c>--domain &lt;имя&gt;</c>, <c>--dc &lt;контроллер&gt;</c>, <c>--user &lt;логин&gt;</c> — подставить значения в окно входа
/// (подразумевают <c>--login</c> и режим «другая учётная запись»). Пароль в командной строке не принимается: его вводят в окне.</item>
/// <item><c>--profile &lt;имя&gt;</c> — отдельная папка данных <c>%APPDATA%\RemoteShadow-&lt;имя&gt;</c> (свои настройки, журнал и
/// сохранённый вход); так можно держать одновременно два окна под разными учётными записями или доменами.</item>
/// </list>
/// Значения принимаются в виде <c>--ключ значение</c> и <c>--ключ=значение</c>.
/// </summary>
public sealed record StartupOptions(bool IsDemo)
{
    public const string DemoArgument = "--demo";
    public const string DemoEnvironmentVariable = "REMOTESHADOW_DEMO";

    public bool ForceLogin { get; init; }
    public string? Domain { get; init; }
    public string? DomainController { get; init; }
    public string? UserName { get; init; }
    public string? Profile { get; init; }

    /// <summary>Нужно ли показать окно входа, а не входить тихо.</summary>
    public bool PromptsForLogin => ForceLogin || Domain is not null || DomainController is not null || UserName is not null;

    public static StartupOptions Parse(IEnumerable<string> args, Func<string, string?> getEnvironmentVariable)
    {
        var list = args.ToList();
        var demo = list.Any(a => string.Equals(a, DemoArgument, StringComparison.OrdinalIgnoreCase))
                   || getEnvironmentVariable(DemoEnvironmentVariable) == "1";

        return new StartupOptions(demo)
        {
            ForceLogin = list.Any(a => string.Equals(a, "--login", StringComparison.OrdinalIgnoreCase)),
            Domain = ValueOf(list, "--domain"),
            DomainController = ValueOf(list, "--dc"),
            UserName = ValueOf(list, "--user"),
            Profile = SafeProfile(ValueOf(list, "--profile")),
        };
    }

    private static string? ValueOf(List<string> args, string key)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return Clean(a[(key.Length + 1)..]);
            if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return Clean(args[i + 1]);
        }

        return null;
    }

    private static string? Clean(string value)
    {
        value = value.Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    /// <summary>Имя профиля становится частью имени папки: оставляем только буквы, цифры, «-», «_» и «.».</summary>
    private static string? SafeProfile(string? name)
    {
        if (name is null) return null;
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('.');
        return safe.Length == 0 ? null : safe;
    }
}
