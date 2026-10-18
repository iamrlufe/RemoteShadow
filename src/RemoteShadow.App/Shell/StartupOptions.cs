namespace RemoteShadow.App.Shell;

/// <summary>Параметры запуска: ключ <c>--demo</c> или переменная окружения <c>REMOTESHADOW_DEMO=1</c> включают демо-режим.</summary>
public sealed record StartupOptions(bool IsDemo)
{
    public const string DemoArgument = "--demo";
    public const string DemoEnvironmentVariable = "REMOTESHADOW_DEMO";

    public static StartupOptions Parse(IEnumerable<string> args, Func<string, string?> getEnvironmentVariable)
    {
        var demo = args.Any(a => string.Equals(a, DemoArgument, StringComparison.OrdinalIgnoreCase))
                   || getEnvironmentVariable(DemoEnvironmentVariable) == "1";
        return new StartupOptions(demo);
    }
}
