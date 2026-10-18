using System.Runtime.InteropServices;

namespace RemoteShadow.Windows.Hosts.Native;

/// <summary>Классы информации для WTSQuerySessionInformationW (WTS_INFO_CLASS).</summary>
internal enum WtsInfoClass
{
    UserName = 5,
    DomainName = 7,
    ClientName = 10,
    SessionInfo = 24,
}

/// <summary>Элемент уровня 1 из WTSEnumerateSessionsExW (WTS_SESSION_INFO_1W). Строки принадлежат общему буферу.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WtsSessionInfo1
{
    public uint ExecEnvId;
    public int State;
    public uint SessionId;
    public IntPtr SessionName;
    public IntPtr HostName;
    public IntPtr UserName;
    public IntPtr DomainName;
    public IntPtr FarmName;
}

/// <summary>Рукописные объявления wtsapi32.dll и advapi32.dll. Все вызовы блокирующие.</summary>
internal static partial class WtsNative
{
    /// <summary>WTS_TYPE_CLASS.WTSTypeSessionInfoLevel1 — для WTSFreeMemoryExW.</summary>
    public const int WtsTypeSessionInfoLevel1 = 2;

    /// <summary>Описатель «текущий сервер» (WTS_CURRENT_SERVER_HANDLE).</summary>
    public static readonly IntPtr CurrentServer = IntPtr.Zero;

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSOpenServerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr WTSOpenServerW(string serverName);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSCloseServer")]
    public static partial void WTSCloseServer(IntPtr server);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSEnumerateSessionsExW(
        IntPtr server, ref uint level, uint filter, out IntPtr sessionInfo, out uint count);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemoryExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSFreeMemoryExW(int typeClass, IntPtr memory, uint numberOfEntries);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQuerySessionInformationW(
        IntPtr server, uint sessionId, WtsInfoClass infoClass, out IntPtr buffer, out uint bytesReturned);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    public static partial void WTSFreeMemory(IntPtr memory);

    /// <summary>titleLength и messageLength — в байтах, без завершающего нуля.</summary>
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSSendMessageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSSendMessageW(
        IntPtr server,
        uint sessionId,
        string title,
        uint titleLength,
        string message,
        uint messageLength,
        uint style,
        uint timeoutSeconds,
        out uint response,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSDisconnectSession", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSDisconnectSession(
        IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSLogoffSession", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSLogoffSession(
        IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("advapi32.dll", EntryPoint = "InitiateSystemShutdownExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitiateSystemShutdownExW(
        string? machineName,
        string? message,
        uint timeoutSeconds,
        [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool rebootAfterShutdown,
        uint reason);
}
