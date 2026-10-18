using System.Buffers.Binary;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Времена из WTSINFOW в виде сырых FILETIME (0 — значение не задано).</summary>
internal readonly record struct WtsTimes(long LogonTime, long LastInputTime, long CurrentTime);

/// <summary>Чистые преобразования данных WTS в модели Core. Вынесены отдельно, чтобы тестироваться без Windows API.</summary>
internal static class WtsConversions
{
    /// <summary>Идентификаторы от 65536 — слушатели (RDP-Tcp), а не сессии пользователей.</summary>
    public const int FirstListenerSessionId = 65536;

    /// <summary>Размер WTSINFOW на x86 и x64: 8 DWORD, три строки WCHAR (32 + 17 + 21), выравнивание до 8, пять LARGE_INTEGER.</summary>
    public const int WtsInfoSize = 216;

    private const int LastInputTimeOffset = 192;
    private const int LogonTimeOffset = 200;
    private const int CurrentTimeOffset = 208;

    // DateTime.FromFileTimeUtc принимает значения от 0 до 2650467743999999999.
    private const long MaxFileTime = 2650467743999999999L;

    /// <summary>WTS_CONNECTSTATE_CLASS → <see cref="SessionState"/>.</summary>
    public static SessionState MapState(int wtsState) => wtsState switch
    {
        0 => SessionState.Active,
        1 => SessionState.Connected,
        2 => SessionState.ConnectQuery,
        3 => SessionState.Shadow,
        4 => SessionState.Disconnected,
        5 => SessionState.Idle,
        6 => SessionState.Listen,
        7 => SessionState.Reset,
        8 => SessionState.Down,
        9 => SessionState.Init,
        _ => SessionState.Unknown,
    };

    /// <summary>Слушателей и сессию 0 (службы) пользователю не показываем.</summary>
    public static bool IsUserSession(int sessionId, SessionState state) =>
        sessionId > 0 && sessionId < FirstListenerSessionId && state != SessionState.Listen;

    /// <summary>
    /// FILETIME (UTC) → локальное время. 0, отрицательные и недопустимые значения → null.
    /// Возвращается <see cref="DateTimeKind.Local"/>: так значение одинаково правильно и при показе как есть,
    /// и после <see cref="DateTime.ToLocalTime"/>.
    /// </summary>
    public static DateTime? FileTimeToLocal(long fileTime)
    {
        if (fileTime <= 0 || fileTime > MaxFileTime) return null;
        return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
    }

    /// <summary>Простой = CurrentTime − LastInputTime. null, если одно из времён не задано или результат отрицательный.</summary>
    public static TimeSpan? IdleTime(long lastInputTime, long currentTime)
    {
        if (lastInputTime <= 0 || currentTime <= 0) return null;
        var ticks = currentTime - lastInputTime;
        return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
    }

    /// <summary>Достаёт времена из буфера WTSINFOW. null, если буфер короче структуры.</summary>
    public static WtsTimes? ParseWtsInfo(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < WtsInfoSize) return null;
        return new WtsTimes(
            BinaryPrimitives.ReadInt64LittleEndian(buffer[LogonTimeOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(buffer[LastInputTimeOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(buffer[CurrentTimeOffset..]));
    }

    /// <summary>Собирает <see cref="SessionInfo"/> из данных перечисления и запросов. Пустые строки → null.</summary>
    public static SessionInfo ToSessionInfo(
        int sessionId, int wtsState, string? winStationName, string? userName, string? domain, string? clientName, WtsTimes? times) =>
        new()
        {
            Id = sessionId,
            State = MapState(wtsState),
            WinStationName = NullIfBlank(winStationName),
            UserName = NullIfBlank(userName),
            Domain = NullIfBlank(domain),
            ClientName = NullIfBlank(clientName),
            LogonTime = times is { } t ? FileTimeToLocal(t.LogonTime) : null,
            IdleTime = times is { } t2 ? IdleTime(t2.LastInputTime, t2.CurrentTime) : null,
        };

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Обрезает текст до <paramref name="maxChars"/> символов, не разрывая суррогатную пару; в конце добавляет «…».</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = maxChars - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..Math.Max(cut, 0)] + "…";
    }

    public const int MaxMessageTitleChars = 100;
    public const int MaxMessageTextChars = 1000;
    public const int MaxShutdownMessageChars = 512;
    public const string DefaultMessageTitle = "Сообщение администратора";

    /// <summary>Максимум задержки в InitiateSystemShutdownEx: 10 лет в секундах.</summary>
    public const uint MaxShutdownTimeoutSeconds = 315360000;

    public static string NormalizeMessageTitle(string? title)
    {
        var t = string.IsNullOrWhiteSpace(title) ? DefaultMessageTitle : title.Trim();
        return Truncate(t, MaxMessageTitleChars);
    }

    public static string NormalizeMessageText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new OperationFailedException("Текст сообщения не может быть пустым.", OperationFailure.InvalidInput);
        return Truncate(text.Trim(), MaxMessageTextChars);
    }

    /// <summary>Текст предупреждения при выключении: пустой → null (окно без пояснения), длинный обрезается.</summary>
    public static string? NormalizeShutdownMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : Truncate(message.Trim(), MaxShutdownMessageChars);

    /// <summary>Задержка → секунды для InitiateSystemShutdownEx (0…10 лет, округление вверх).</summary>
    public static uint ShutdownTimeoutSeconds(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return 0;
        var seconds = Math.Ceiling(delay.TotalSeconds);
        return seconds >= MaxShutdownTimeoutSeconds ? MaxShutdownTimeoutSeconds : (uint)seconds;
    }
}
