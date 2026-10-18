using System.Runtime.InteropServices;
using RemoteShadow.Core.Models;
using RemoteShadow.Windows.Hosts.Native;

namespace RemoteShadow.Windows.Hosts;

/// <summary>
/// Блокирующие операции WTS и завершения работы. Вызывающий отвечает за пул потоков, дедлайн и олицетворение.
/// Интерфейс нужен, чтобы тестировать <see cref="WtsSessionService"/> без настоящих компьютеров.
/// </summary>
internal interface IWtsApi
{
    IReadOnlyList<SessionInfo> QuerySessions(string host);
    void SendMessage(string host, int sessionId, string title, string text);
    void Disconnect(string host, int sessionId);
    void Logoff(string host, int sessionId);
    void Shutdown(string host, bool reboot, uint timeoutSeconds, string? message);
}

/// <summary>Настоящая реализация поверх wtsapi32.dll и advapi32.dll. Ошибки — <see cref="WtsException"/>.</summary>
internal sealed class WtsApi : IWtsApi
{
    private const uint MbOk = 0x0;
    private const uint MbIconInformation = 0x40;

    // SHTDN_REASON_MAJOR_OTHER (0) | SHTDN_REASON_FLAG_PLANNED (0x80000000).
    private const uint ShutdownReasonOtherPlanned = 0x80000000;

    public IReadOnlyList<SessionInfo> QuerySessions(string host) => QuerySessionsCore(host);

    public void SendMessage(string host, int sessionId, string title, string text)
    {
        WithServer(host, server =>
        {
            // Длины передаются в байтах (UTF-16), без завершающего нуля. bWait = false: не ждём ответа пользователя.
            var ok = WtsNative.WTSSendMessageW(
                server, (uint)sessionId, title, (uint)(title.Length * 2), text, (uint)(text.Length * 2),
                MbOk | MbIconInformation, 0, out _, false);
            if (!ok) throw new WtsException("WTSSendMessageW", Marshal.GetLastPInvokeError());
        });
    }

    public void Disconnect(string host, int sessionId)
    {
        WithServer(host, server =>
        {
            if (!WtsNative.WTSDisconnectSession(server, (uint)sessionId, false))
                throw new WtsException("WTSDisconnectSession", Marshal.GetLastPInvokeError());
        });
    }

    public void Logoff(string host, int sessionId)
    {
        WithServer(host, server =>
        {
            if (!WtsNative.WTSLogoffSession(server, (uint)sessionId, false))
                throw new WtsException("WTSLogoffSession", Marshal.GetLastPInvokeError());
        });
    }

    public void Shutdown(string host, bool reboot, uint timeoutSeconds, string? message)
    {
        // Приложения не закрываем принудительно: пользователь успеет сохранить работу, а зависшее приложение отменит выключение.
        if (!WtsNative.InitiateSystemShutdownExW(host, message, timeoutSeconds, false, reboot, ShutdownReasonOtherPlanned))
            throw new WtsException("InitiateSystemShutdownExW", Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// Сессии сервера. <paramref name="serverName"/> = null — локальный компьютер (WTS_CURRENT_SERVER_HANDLE), без RPC;
    /// так проверяется раскладка структур на машине разработчика.
    /// </summary>
    internal static IReadOnlyList<SessionInfo> QuerySessionsCore(string? serverName)
    {
        var raw = new List<RawSession>();
        var result = new List<SessionInfo>();

        void Run(IntPtr server)
        {
            EnumerateSessions(server, raw);

            foreach (var s in raw)
            {
                var state = WtsConversions.MapState(s.State);
                if (!WtsConversions.IsUserSession(s.Id, state)) continue;

                var user = s.UserName;
                var domain = s.Domain;
                if (string.IsNullOrWhiteSpace(user)) user = QueryString(server, s.Id, WtsInfoClass.UserName);
                if (!string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(domain))
                    domain = QueryString(server, s.Id, WtsInfoClass.DomainName);

                var client = QueryString(server, s.Id, WtsInfoClass.ClientName);
                var times = string.IsNullOrWhiteSpace(user) ? null : QueryTimes(server, s.Id);

                result.Add(WtsConversions.ToSessionInfo(s.Id, s.State, s.WinStationName, user, domain, client, times));
            }
        }

        if (serverName is null) Run(WtsNative.CurrentServer);
        else WithServer(serverName, Run);

        result.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return result;
    }

    private static void WithServer(string host, Action<IntPtr> action)
    {
        var server = WtsNative.WTSOpenServerW(host);
        if (server == IntPtr.Zero) throw new WtsException("WTSOpenServerW", Marshal.GetLastPInvokeError());
        try
        {
            action(server);
        }
        finally
        {
            WtsNative.WTSCloseServer(server);
        }
    }

    private static unsafe void EnumerateSessions(IntPtr server, List<RawSession> into)
    {
        uint level = 1;
        if (!WtsNative.WTSEnumerateSessionsExW(server, ref level, 0, out var buffer, out var count))
            throw new WtsException("WTSEnumerateSessionsExW", Marshal.GetLastPInvokeError());

        try
        {
            var items = (WtsSessionInfo1*)buffer;
            for (uint i = 0; i < count; i++)
            {
                var item = items[i];
                into.Add(new RawSession(
                    (int)item.SessionId,
                    item.State,
                    Marshal.PtrToStringUni(item.SessionName),
                    Marshal.PtrToStringUni(item.UserName),
                    Marshal.PtrToStringUni(item.DomainName)));
            }
        }
        finally
        {
            WtsNative.WTSFreeMemoryExW(WtsNative.WtsTypeSessionInfoLevel1, buffer, count);
        }
    }

    /// <summary>Строка из WTSQuerySessionInformationW. Сбой отдельной сессии (она могла завершиться) → null, а не ошибка всего списка.</summary>
    private static string? QueryString(IntPtr server, int sessionId, WtsInfoClass infoClass)
    {
        if (!WtsNative.WTSQuerySessionInformationW(server, (uint)sessionId, infoClass, out var buffer, out _))
            return null;
        try
        {
            return WtsConversions.NullIfBlank(Marshal.PtrToStringUni(buffer));
        }
        finally
        {
            WtsNative.WTSFreeMemory(buffer);
        }
    }

    private static unsafe WtsTimes? QueryTimes(IntPtr server, int sessionId)
    {
        if (!WtsNative.WTSQuerySessionInformationW(server, (uint)sessionId, WtsInfoClass.SessionInfo, out var buffer, out var bytes))
            return null;
        try
        {
            return WtsConversions.ParseWtsInfo(new ReadOnlySpan<byte>((void*)buffer, (int)bytes));
        }
        finally
        {
            WtsNative.WTSFreeMemory(buffer);
        }
    }

    private readonly record struct RawSession(int Id, int State, string? WinStationName, string? UserName, string? Domain);
}
