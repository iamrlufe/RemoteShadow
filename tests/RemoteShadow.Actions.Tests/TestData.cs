namespace RemoteShadow.Actions.Tests;

public static class TestData
{
    /// <summary>Имена, которые нельзя передавать во внешние программы: ключи командной строки, пробелы, кавычки, управляющие символы.</summary>
    public static readonly string[] BadHosts =
    [
        "-evil",
        "/v:x",
        "pc 1",
        "pc\"1",
        "pc1 /control",
        "pc1;calc",
        "pc1&calc",
        "pc1|calc",
        @"\\pc1",
        "host:3389",
        "[::1]",
        "pc1\n",
        "pc1\r\n/v:x",
        "pc1\t",
        "",
        "   ",
    ];

    public static TheoryData<string> BadHostData()
    {
        var data = new TheoryData<string>();
        foreach (var host in BadHosts) data.Add(host);
        return data;
    }

    /// <summary>Каждое действие, которое передаёт имя компьютера наружу.</summary>
    public static readonly string[] HostPassingActions =
    [
        "shadow-view",
        "shadow-control",
        "shadow-view-noconsent",
        "rdp",
        "ssh",
        "open-admin-share",
        "send-message",
        "disconnect",
        "logoff",
        "reboot",
        "shutdown",
        "apply-fix",
    ];

    public static TheoryData<string, string> BadHostPerAction()
    {
        var data = new TheoryData<string, string>();
        foreach (var action in HostPassingActions)
            foreach (var host in BadHosts)
                data.Add(action, host);
        return data;
    }
}
