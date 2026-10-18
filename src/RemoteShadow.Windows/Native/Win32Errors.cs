namespace RemoteShadow.Windows.Native;

/// <summary>Коды ошибок Win32, которые разбираются в этом проекте.</summary>
internal static class Win32Errors
{
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int InvalidParameter = 87;
    public const int NotFound = 1168;
    public const int LogonFailure = 1326;
    public const int AccountRestriction = 1327;
    public const int AccountDisabled = 1331;
    public const int AccountLockedOut = 1909;
    public const int ServiceDoesNotExist = 1060;
    public const int ServiceDisabled = 1058;
    public const int ServiceNotActive = 1062;
    public const int ElevationRequired = 740;
}
