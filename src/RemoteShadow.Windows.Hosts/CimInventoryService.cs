using System.Globalization;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Windows.Hosts;

/// <summary>
/// Сведения об устройстве по WinRM (CIM): ОС, производитель, модель, серийный номер, MAC, процессор, память, диски,
/// текущий пользователь, время загрузки. Работает под учётной записью подключения. Ошибки превращаются в текст причины.
/// </summary>
public sealed class CimInventoryService : IDeviceInventoryService
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(2);

    private readonly ICurrentConnection _connection;
    private readonly IHostProbe _probe;

    public CimInventoryService(ICurrentConnection connection, IHostProbe probe)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<DeviceInventory> CollectAsync(string host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(host)) return new DeviceInventory { Error = "не указан адрес" };

        IDomainConnection? connection = _connection.Current;
        if (connection is null) return new DeviceInventory { Error = "нет подключения к домену" };

        bool winRm;
        try
        {
            winRm = await _probe.IsPortOpenAsync(host, 5985, PortTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            winRm = false;
        }

        if (!winRm) return new DeviceInventory { Error = "WinRM закрыт (порт 5985)" };

        var work = Task.Run(() => Collect(host, connection.Credential), CancellationToken.None);
        var finished = await Task.WhenAny(work, Task.Delay(Deadline, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (finished != work) return new DeviceInventory { Error = "WinRM не ответил вовремя" };
        return await work.ConfigureAwait(false);
    }

    private static DeviceInventory Collect(string host, AccountCredential? credential)
    {
        try
        {
            using var cim = CimRemote.Connect(host, credential);

            var details = new Dictionary<string, string>();
            string? hostname = null, domain = null, osName = null, osVersion = null, maker = null, model = null, serial = null, mac = null;

            var os = First(cim, "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, CSName FROM Win32_OperatingSystem");
            if (os is not null)
            {
                osName = Text(os, "Caption");
                osVersion = Join(" ", Text(os, "Version"), Text(os, "BuildNumber") is { } b ? $"(сборка {b})" : null);
                hostname = Text(os, "CSName");
                Add(details, "Архитектура ОС", Text(os, "OSArchitecture"));
                if (os.TryGetValue("LastBootUpTime", out var boot) && boot is DateTime bootTime)
                    Add(details, "Загружен", bootTime.ToString("g", CultureInfo.CurrentCulture));
            }

            var cs = First(cim, "SELECT Manufacturer, Model, Domain, UserName, TotalPhysicalMemory, Name FROM Win32_ComputerSystem");
            if (cs is not null)
            {
                maker = Text(cs, "Manufacturer");
                model = Text(cs, "Model");
                domain = Text(cs, "Domain");
                hostname ??= Text(cs, "Name");
                Add(details, "Пользователь", Text(cs, "UserName"));
                if (cs.TryGetValue("TotalPhysicalMemory", out var ram) && ram is not null && ulong.TryParse(Convert.ToString(ram, CultureInfo.InvariantCulture), out var bytes))
                    Add(details, "Память", FormatSize(bytes));
            }

            serial = Text(First(cim, "SELECT SerialNumber FROM Win32_BIOS"), "SerialNumber");

            var cpu = First(cim, "SELECT Name, NumberOfCores FROM Win32_Processor");
            if (cpu is not null) Add(details, "Процессор", Join(", ", Text(cpu, "Name"), Text(cpu, "NumberOfCores") is { } c ? $"ядер: {c}" : null));

            var disks = Rows(cim, "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3");
            if (disks.Count > 0)
            {
                var parts = disks.Select(d =>
                {
                    ulong.TryParse(Convert.ToString(d.GetValueOrDefault("Size"), CultureInfo.InvariantCulture), out var size);
                    ulong.TryParse(Convert.ToString(d.GetValueOrDefault("FreeSpace"), CultureInfo.InvariantCulture), out var free);
                    return $"{Text(d, "DeviceID")} {FormatSize(size)} (свободно {FormatSize(free)})";
                });
                Add(details, "Диски", string.Join("; ", parts));
            }

            var nics = Rows(cim, "SELECT MACAddress, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            mac = nics.Select(n => Text(n, "MACAddress")).FirstOrDefault(m => !string.IsNullOrEmpty(m))?.Replace('-', ':');
            var ips = nics.SelectMany(n => n.GetValueOrDefault("IPAddress") is string[] a ? a : Array.Empty<string>()).Where(a => !a.Contains(':')).ToList();
            if (ips.Count > 0) Add(details, "IP-адреса", string.Join(", ", ips));

            return new DeviceInventory
            {
                Hostname = hostname, Domain = domain, OsName = osName, OsVersion = osVersion,
                Manufacturer = maker, Model = model, SerialNumber = serial, Mac = mac, Details = details,
            };
        }
        catch (CimRemoteException ex)
        {
            return new DeviceInventory { Error = ex.Kind == CimFailureKind.AccessDenied ? "нет прав (доступ запрещён)" : ex.Message };
        }
        catch (Exception ex)
        {
            return new DeviceInventory { Error = CimHelpers.ShortMessage(ex.Message) };
        }
    }

    private static IReadOnlyDictionary<string, object?>? First(CimRemote cim, string wql) => Rows(cim, wql).FirstOrDefault();

    // Фатальная ошибка (нет связи, нет прав) поднимается наверх; отсутствие класса пропускается: часть сведений лучше, чем ничего.
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(CimRemote cim, string wql)
    {
        try
        {
            return cim.QueryRows(wql);
        }
        catch (CimRemoteException ex) when (!ex.IsFatal)
        {
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }
    }

    private static string? Text(IReadOnlyDictionary<string, object?>? row, string key)
    {
        if (row is null || !row.TryGetValue(key, out var value) || value is null) return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static void Add(Dictionary<string, string> details, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) details[key] = value;
    }

    private static string? Join(string separator, params string?[] parts)
    {
        var text = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    internal static string FormatSize(ulong bytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        return bytes >= gb * 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / (gb * 1024):0.#} ТБ")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / gb:0.#} ГБ");
    }
}
