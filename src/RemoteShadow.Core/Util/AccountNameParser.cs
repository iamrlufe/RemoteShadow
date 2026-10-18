namespace RemoteShadow.Core.Util;

/// <param name="Domain">NetBIOS-часть из CORP\ivanov, либо null.</param>
/// <param name="UserName">Логин без домена.</param>
/// <param name="UpnSuffix">Часть после @ из ivanov@corp.local, либо null.</param>
public sealed record ParsedAccountName(string? Domain, string UserName, string? UpnSuffix)
{
    public bool IsUpn => UpnSuffix is not null;
}

public static class AccountNameParser
{
    /// <summary>Разбирает "ivanov", "CORP\ivanov" и "ivanov@corp.local".</summary>
    public static ParsedAccountName Parse(string input)
    {
        input = input.Trim();

        var slash = input.IndexOf('\\');
        if (slash > 0 && slash < input.Length - 1)
            return new ParsedAccountName(input[..slash], input[(slash + 1)..], null);

        var at = input.LastIndexOf('@');
        if (at > 0 && at < input.Length - 1)
            return new ParsedAccountName(null, input[..at], input[(at + 1)..]);

        return new ParsedAccountName(null, input, null);
    }
}
