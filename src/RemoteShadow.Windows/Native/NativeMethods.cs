using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteShadow.Windows.Native;

/// <summary>Рукописные объявления Win32 API (CsWin32 не используем). Только то, что нужно этому проекту.</summary>
internal static unsafe partial class NativeMethods
{
    // netapi32: NETSETUP_JOIN_STATUS
    internal const int NetSetupDomainName = 3;

    // dnsapi
    internal const ushort DnsTypeSrv = 0x0021;
    internal const int DnsFreeRecordList = 1;

    // advapi32: LogonUser
    internal const int Logon32LogonNewCredentials = 9;
    internal const int Logon32ProviderWinNT50 = 3;

    // advapi32: CreateProcessWithLogonW
    internal const uint LogonNetCredentialsOnly = 2;

    // advapi32: Credential Manager
    internal const uint CredTypeGeneric = 1;
    internal const uint CredPersistLocalMachine = 2;

    // kernel32: COMPUTER_NAME_FORMAT
    internal const int ComputerNameDnsDomain = 2;

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int joinStatus);

    [LibraryImport("netapi32.dll")]
    internal static partial int NetApiBufferFree(IntPtr buffer);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr records, IntPtr reserved);

    [LibraryImport("dnsapi.dll")]
    internal static partial void DnsRecordListFree(IntPtr recordList, int freeType);

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LogonUser(
        string userName,
        string? domain,
        string password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessWithLogon(
        string userName,
        string? domain,
        string password,
        uint logonFlags,
        string? applicationName,
        char* commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        in StartupInfoW startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredWrite(in CredentialW credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    internal static partial void CredFree(IntPtr buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetComputerNameExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetComputerNameEx(int nameType, char* buffer, ref uint size);
}

/// <summary>STARTUPINFOW. Строковые поля не используются, поэтому указатели.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoW
{
    public int Cb;
    public IntPtr Reserved;
    public IntPtr Desktop;
    public IntPtr Title;
    public int X;
    public int Y;
    public int XSize;
    public int YSize;
    public int XCountChars;
    public int YCountChars;
    public int FillAttribute;
    public int Flags;
    public short ShowWindow;
    public short Reserved2Size;
    public IntPtr Reserved2;
    public IntPtr StdInput;
    public IntPtr StdOutput;
    public IntPtr StdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public IntPtr Process;
    public IntPtr Thread;
    public int ProcessId;
    public int ThreadId;
}

/// <summary>CREDENTIALW. FILETIME разложен на две половины, чтобы не получить лишнее выравнивание.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CredentialW
{
    public uint Flags;
    public uint Type;
    public IntPtr TargetName;
    public IntPtr Comment;
    public uint LastWrittenLow;
    public uint LastWrittenHigh;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public IntPtr Attributes;
    public IntPtr TargetAlias;
    public IntPtr UserName;
}
