using System.Globalization;
using RemoteShadow.Core;

namespace RemoteShadow.Modules;

/// <summary>
/// Аргументы командной строки mstsc и имена внешних программ. Только чистые функции: имя компьютера
/// проверяется перед подстановкой, идентификатор сессии не может быть отрицательным.
/// </summary>
public static class MstscCommand
{
    public const string MstscExe = "mstsc.exe";
    public const string SshExe = "ssh.exe";
    public const string WindowsTerminalExe = "wt.exe";
    public const string ExplorerExe = "explorer.exe";

    /// <summary>Shadow-подключение к сессии: <c>/v:host /shadow:id [/control] [/noConsentPrompt]</c>.</summary>
    /// <param name="control">true — управление, false — только просмотр.</param>
    /// <param name="noConsent">true — без запроса согласия у пользователя (разрешает только политика Shadow на компьютере).</param>
    public static IReadOnlyList<string> Shadow(string host, int sessionId, bool control, bool noConsent)
    {
        var args = new List<string>(4)
        {
            $"/v:{HostGuard.Require(host)}",
            $"/shadow:{RequireSessionId(sessionId).ToString(CultureInfo.InvariantCulture)}",
        };
        if (control) args.Add("/control");
        if (noConsent) args.Add("/noConsentPrompt");
        return args;
    }

    /// <summary>Обычное RDP-подключение: <c>/v:host</c>.</summary>
    public static IReadOnlyList<string> Rdp(string host) => [$"/v:{HostGuard.Require(host)}"];

    private static int RequireSessionId(int sessionId) =>
        sessionId >= 0
            ? sessionId
            : throw new OperationFailedException($"Недопустимый номер сессии: {sessionId.ToString(CultureInfo.InvariantCulture)}.", OperationFailure.InvalidInput);
}
