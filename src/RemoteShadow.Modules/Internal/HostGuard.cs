using RemoteShadow.Core;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Modules;

/// <summary>Общие проверки перед передачей данных во внешние программы и сервисы.</summary>
internal static class HostGuard
{
    /// <summary>
    /// Имя компьютера через <see cref="HostNameValidator.Require"/> плюс отказ на управляющих символах:
    /// якорь «$» в регулярном выражении Core пропускает перевод строки в конце имени («pc1\n»).
    /// </summary>
    public static string Require(string? host)
    {
        var valid = HostNameValidator.Require(host);
        if (valid.Any(char.IsControl))
            throw new OperationFailedException("Недопустимое имя компьютера: в нём есть управляющие символы.", OperationFailure.InvalidInput);
        return valid;
    }

    /// <summary>Путь к программе, найденный запускателем, иначе просто имя — пусть ищет сама система.</summary>
    public static string ResolveExecutable(IProcessLauncher launcher, string fileName) =>
        launcher.FindExecutable(fileName) ?? fileName;
}
