namespace RemoteShadow.Windows;

/// <summary>Флаги userAccountControl и msDS-User-Account-Control-Computed.</summary>
internal static class UserAccountControl
{
    /// <summary>ACCOUNTDISABLE: учётная запись отключена.</summary>
    public const int AccountDisable = 0x2;

    /// <summary>LOCKOUT: учётная запись заблокирована (читается из msDS-User-Account-Control-Computed).</summary>
    public const int Lockout = 0x10;

    /// <summary>DONT_EXPIRE_PASSWORD: пароль не истекает.</summary>
    public const int DontExpirePassword = 0x10000;

    public static bool IsDisabled(int flags) => (flags & AccountDisable) != 0;

    public static bool IsLockedOut(int flags) => (flags & Lockout) != 0;

    public static bool NeverExpires(int flags) => (flags & DontExpirePassword) != 0;

    /// <summary>Флаги с включённой или отключённой учётной записью; остальные биты не меняются.</summary>
    public static int WithEnabled(int flags, bool enabled) =>
        enabled ? flags & ~AccountDisable : flags | AccountDisable;
}
