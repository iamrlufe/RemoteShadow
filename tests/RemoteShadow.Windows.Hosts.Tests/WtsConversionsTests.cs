using System.Buffers.Binary;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts.Tests;

public class WtsConversionsTests
{
    [Theory]
    [InlineData(0, SessionState.Active)]
    [InlineData(1, SessionState.Connected)]
    [InlineData(2, SessionState.ConnectQuery)]
    [InlineData(3, SessionState.Shadow)]
    [InlineData(4, SessionState.Disconnected)]
    [InlineData(5, SessionState.Idle)]
    [InlineData(6, SessionState.Listen)]
    [InlineData(7, SessionState.Reset)]
    [InlineData(8, SessionState.Down)]
    [InlineData(9, SessionState.Init)]
    [InlineData(10, SessionState.Unknown)]
    [InlineData(-1, SessionState.Unknown)]
    public void MapState(int wts, SessionState expected) =>
        Assert.Equal(expected, WtsConversions.MapState(wts));

    [Theory]
    [InlineData(0, SessionState.Disconnected, false)]
    [InlineData(0, SessionState.Active, false)]
    [InlineData(1, SessionState.Active, true)]
    [InlineData(2, SessionState.Disconnected, true)]
    [InlineData(65535, SessionState.Active, true)]
    [InlineData(65536, SessionState.Listen, false)]
    [InlineData(65537, SessionState.Active, false)]
    [InlineData(3, SessionState.Listen, false)]
    public void IsUserSession(int id, SessionState state, bool expected) =>
        Assert.Equal(expected, WtsConversions.IsUserSession(id, state));

    [Fact]
    public void FileTime_zero_and_invalid_are_null()
    {
        Assert.Null(WtsConversions.FileTimeToLocal(0));
        Assert.Null(WtsConversions.FileTimeToLocal(-5));
        Assert.Null(WtsConversions.FileTimeToLocal(long.MaxValue));
    }

    [Fact]
    public void FileTime_converts_utc_to_local()
    {
        var utc = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

        var local = WtsConversions.FileTimeToLocal(utc.ToFileTimeUtc());

        Assert.NotNull(local);
        Assert.Equal(DateTimeKind.Local, local!.Value.Kind);
        Assert.Equal(utc, local.Value.ToUniversalTime());
        Assert.Equal(utc.ToLocalTime(), local.Value);
    }

    [Fact]
    public void Idle_is_current_minus_last_input()
    {
        var now = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var last = now - TimeSpan.FromMinutes(17).Ticks;

        Assert.Equal(TimeSpan.FromMinutes(17), WtsConversions.IdleTime(last, now));
        Assert.Equal(TimeSpan.Zero, WtsConversions.IdleTime(now, now));
    }

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(100L, 0L)]
    [InlineData(0L, 0L)]
    [InlineData(200L, 100L)]
    public void Idle_is_null_when_unknown_or_negative(long lastInput, long current) =>
        Assert.Null(WtsConversions.IdleTime(lastInput, current));

    [Fact]
    public void ParseWtsInfo_reads_times_at_documented_offsets()
    {
        var buffer = new byte[WtsConversions.WtsInfoSize];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(192), 111);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(200), 222);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(208), 333);

        var times = WtsConversions.ParseWtsInfo(buffer);

        Assert.Equal(new WtsTimes(LogonTime: 222, LastInputTime: 111, CurrentTime: 333), times);
    }

    [Fact]
    public void ParseWtsInfo_short_buffer_is_null() =>
        Assert.Null(WtsConversions.ParseWtsInfo(new byte[WtsConversions.WtsInfoSize - 1]));

    [Fact]
    public void ToSessionInfo_fills_all_fields()
    {
        var logon = new DateTime(2026, 3, 14, 8, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var current = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var lastInput = current - TimeSpan.FromMinutes(5).Ticks;

        var s = WtsConversions.ToSessionInfo(2, 0, "rdp-tcp#4", "ivanov", "CORP", "LAPTOP-7", new WtsTimes(logon, lastInput, current));

        Assert.Equal(2, s.Id);
        Assert.Equal(SessionState.Active, s.State);
        Assert.Equal("rdp-tcp#4", s.WinStationName);
        Assert.Equal(@"CORP\ivanov", s.AccountName);
        Assert.Equal("LAPTOP-7", s.ClientName);
        Assert.Equal(TimeSpan.FromMinutes(5), s.IdleTime);
        Assert.Equal(DateTime.FromFileTimeUtc(logon).ToLocalTime(), s.LogonTime);
        Assert.True(s.CanShadow);
    }

    [Fact]
    public void ToSessionInfo_blank_strings_become_null_and_no_times_means_unknown()
    {
        var s = WtsConversions.ToSessionInfo(1, 4, "Console", "  ", "", null, null);

        Assert.Equal(SessionState.Disconnected, s.State);
        Assert.Null(s.UserName);
        Assert.Null(s.Domain);
        Assert.Null(s.ClientName);
        Assert.Null(s.LogonTime);
        Assert.Null(s.IdleTime);
        Assert.False(s.HasUser);
    }

    [Fact]
    public void Truncate_keeps_short_text_and_cuts_long_with_ellipsis()
    {
        Assert.Equal("abc", WtsConversions.Truncate("abc", 3));
        Assert.Equal("ab…", WtsConversions.Truncate("abcdef", 3));
    }

    [Fact]
    public void Truncate_does_not_split_surrogate_pair()
    {
        var text = "ab😀cd";

        var cut = WtsConversions.Truncate(text, 4);

        Assert.Equal("ab…", cut);
        Assert.True(cut.All(c => !char.IsSurrogate(c)));
    }

    [Fact]
    public void Message_title_defaults_and_truncates()
    {
        Assert.Equal(WtsConversions.DefaultMessageTitle, WtsConversions.NormalizeMessageTitle("  "));
        Assert.Equal(WtsConversions.DefaultMessageTitle, WtsConversions.NormalizeMessageTitle(null));
        Assert.Equal(WtsConversions.MaxMessageTitleChars, WtsConversions.NormalizeMessageTitle(new string('я', 500)).Length);
    }

    [Fact]
    public void Message_text_truncates_and_rejects_empty()
    {
        Assert.Equal(WtsConversions.MaxMessageTextChars, WtsConversions.NormalizeMessageText(new string('я', 5000)).Length);

        var ex = Assert.Throws<OperationFailedException>(() => WtsConversions.NormalizeMessageText("   "));
        Assert.Equal(OperationFailure.InvalidInput, ex.Failure);
    }

    [Fact]
    public void Shutdown_message_empty_is_null_and_long_is_cut()
    {
        Assert.Null(WtsConversions.NormalizeShutdownMessage(null));
        Assert.Null(WtsConversions.NormalizeShutdownMessage(" "));
        Assert.Equal("Перезагрузка", WtsConversions.NormalizeShutdownMessage(" Перезагрузка "));
        Assert.Equal(WtsConversions.MaxShutdownMessageChars, WtsConversions.NormalizeShutdownMessage(new string('x', 2000))!.Length);
    }

    [Theory]
    [InlineData(0.0, 0u)]
    [InlineData(-30.0, 0u)]
    [InlineData(60.0, 60u)]
    [InlineData(60.2, 61u)]
    public void ShutdownTimeoutSeconds(double seconds, uint expected) =>
        Assert.Equal(expected, WtsConversions.ShutdownTimeoutSeconds(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ShutdownTimeoutSeconds_is_capped_at_ten_years() =>
        Assert.Equal(315360000u, WtsConversions.ShutdownTimeoutSeconds(TimeSpan.FromDays(10000)));
}
