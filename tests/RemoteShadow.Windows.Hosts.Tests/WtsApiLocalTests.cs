using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteShadow.Windows.Hosts.Native;

namespace RemoteShadow.Windows.Hosts.Tests;

/// <summary>
/// Проверка раскладки структур WTS на локальном компьютере (без RPC и без сети): WTS_CURRENT_SERVER_HANDLE.
/// Если WTS в среде недоступен или тест запущен из сессии 0 (служба), тест молча пропускается.
/// </summary>
public class WtsApiLocalTests
{
    private const uint CurrentSession = uint.MaxValue; // WTS_CURRENT_SESSION

    private static bool InteractiveSession => Process.GetCurrentProcess().SessionId > 0;

    [Fact]
    public void WtsInfo_buffer_has_documented_size_and_utc_times()
    {
        if (!InteractiveSession) return;

        if (!WtsNative.WTSQuerySessionInformationW(WtsNative.CurrentServer, CurrentSession, WtsInfoClass.SessionInfo, out var buffer, out var bytes))
            return;

        byte[] data;
        try
        {
            data = new byte[bytes];
            Marshal.Copy(buffer, data, 0, (int)bytes);
        }
        finally
        {
            WtsNative.WTSFreeMemory(buffer);
        }

        Assert.Equal((uint)WtsConversions.WtsInfoSize, bytes);

        var times = WtsConversions.ParseWtsInfo(data);
        Assert.NotNull(times);

        // CurrentTime — «сейчас» по часам сервера в UTC: расхождение с UtcNow больше нескольких минут
        // означало бы неверные смещения или локальное, а не UTC-время.
        var now = DateTime.UtcNow.ToFileTimeUtc();
        Assert.InRange(times!.Value.CurrentTime, now - TimeSpan.FromMinutes(5).Ticks, now + TimeSpan.FromMinutes(5).Ticks);
        Assert.InRange(times.Value.LogonTime, 1, times.Value.CurrentTime);
        Assert.InRange(times.Value.LastInputTime, 1, times.Value.CurrentTime);
    }

    [Fact]
    public void Local_enumeration_returns_current_user_session_without_services_and_listeners()
    {
        if (!InteractiveSession) return;

        IReadOnlyList<RemoteShadow.Core.Models.SessionInfo> sessions;
        try
        {
            sessions = WtsApi.QuerySessionsCore(null);
        }
        catch (WtsException)
        {
            return;
        }

        var mine = sessions.SingleOrDefault(s => s.Id == Process.GetCurrentProcess().SessionId);
        Assert.NotNull(mine);
        Assert.True(mine!.HasUser);
        Assert.NotNull(mine.LogonTime);
        Assert.InRange(mine.LogonTime!.Value, new DateTime(2000, 1, 1), DateTime.Now.AddMinutes(5));
        Assert.NotNull(mine.IdleTime);
        Assert.InRange(mine.IdleTime!.Value, TimeSpan.Zero, TimeSpan.FromDays(365));

        Assert.DoesNotContain(sessions, s => s.Id == 0 || s.Id >= WtsConversions.FirstListenerSessionId);
        Assert.DoesNotContain(sessions, s => s.State == RemoteShadow.Core.Models.SessionState.Listen);
        Assert.Equal(sessions.Select(s => s.Id).OrderBy(i => i), sessions.Select(s => s.Id));
    }
}
