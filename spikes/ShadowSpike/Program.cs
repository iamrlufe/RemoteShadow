using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Services;
using RemoteShadow.Windows;
using RemoteShadow.Windows.Hosts;
using ShadowSpike;

Console.OutputEncoding = Encoding.UTF8;

var parsed = ArgParser.Parse(args);
if (parsed.ShowHelp)
{
    Console.WriteLine(ArgParser.Usage);
    return 0;
}

if (!parsed.Ok)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ArgParser.Usage);
    return 2;
}

var options = parsed.Options!;

// Пароль живёт только в этой переменной. Берём из переменной окружения или запрашиваем без эха.
string? password = null;
if (options.UserName is not null)
{
    password = Environment.GetEnvironmentVariable("SPIKE_PASSWORD");
    if (string.IsNullOrEmpty(password))
        password = ConsoleIo.ReadPassword($"Пароль для {options.UserName}: ");
}

var secrets = new List<string>();
if (!string.IsNullOrEmpty(password)) secrets.Add(password);
string Redact(string text) => ReportBuilder.Redact(text, secrets);

var services = new ServiceCollection();
services.AddSingleton<ICurrentConnection, CurrentConnection>();
services.AddSingleton<ISettingsStore, MemorySettingsStore>();
services.AddRemoteShadowWindows();
services.AddRemoteShadowWindowsHosts();

using var provider = services.BuildServiceProvider();

var triggers = new HashSet<string>();
var runner = new StepRunner(Redact, triggers);
var checks = new SpikeChecks(options, provider, runner, password);

Console.WriteLine("ShadowSpike: проверка допущений RemoteShadow в реальном домене.");
Console.WriteLine("Операции записи в каталог не выполняются.");

try
{
    await checks.RunAllAsync();
}
finally
{
    provider.GetRequiredService<ICurrentConnection>().Set(null);
}

Console.WriteLine();
Console.WriteLine("==== Итоги ====");
Console.Write(ReportBuilder.BuildConsoleTable(runner.Results));

var now = DateTime.Now;
var data = new ReportData(
    now,
    "ShadowSpike " + string.Join(' ', args),
    checks.EnvironmentFacts,
    runner.Results,
    triggers,
    secrets);

var path = options.ResolveReportPath(now);
try
{
    await File.WriteAllTextAsync(path, ReportBuilder.BuildMarkdown(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    Console.WriteLine($"Отчёт: {Path.GetFullPath(path)}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Не удалось записать отчёт {path}: {ex.Message}");
    return 3;
}

return runner.Results.Any(r => r.Status == StepStatus.Fail) ? 1 : 0;
