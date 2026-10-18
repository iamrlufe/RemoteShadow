using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Infrastructure;

/// <summary>Содержимое файла кэша индекса на диске. Секретов и статуса «в сети» здесь нет.</summary>
internal sealed class ComputerCacheFile
{
    /// <summary>Версия формата. Файл другой версии игнорируется и перезаписывается после первой синхронизации.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; }
    public string? Domain { get; set; }

    /// <summary>Контроллер домена, с которого шла последняя синхронизация.</summary>
    public string? DomainController { get; set; }

    /// <summary>Начало последней успешной синхронизации (UTC): от него считается ChangedSince частичного обновления.</summary>
    public DateTime? LastSyncUtc { get; set; }

    public DateTime? LastFullSyncUtc { get; set; }

    /// <summary>Были ли в последней полной загрузке отключённые компьютеры.</summary>
    public bool ShowDisabled { get; set; }

    public List<CachedComputer> Computers { get; set; } = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Кириллица пишется как есть (2 байта на букву), а не последовательностью из 6 символов; файл не попадает в HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Имя файла кэша для домена: только безопасные для файловой системы символы и не зарезервированные имена Windows.</summary>
    public static string FileNameFor(string domain)
    {
        var sb = new StringBuilder(domain.Length);
        foreach (var c in domain.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        var name = sb.ToString().Trim('.');
        if (name.Length == 0) name = "domain";
        if (name.Length > 100) name = name[..100];

        var firstPart = name.Split('.')[0];
        if (IsReservedDeviceName(firstPart)) name = "_" + name;

        return name + ".json";
    }

    private static bool IsReservedDeviceName(string name) =>
        name is "con" or "prn" or "aux" or "nul"
        || (name.Length == 4 && (name.StartsWith("com", StringComparison.Ordinal) || name.StartsWith("lpt", StringComparison.Ordinal)) && char.IsDigit(name[3]));
}

/// <summary>Поля компьютера, которые хранятся в кэше. Вычисляемые свойства (HostName, ParentDn) и статус «в сети» не сохраняются.</summary>
internal sealed class CachedComputer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DnsName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OperatingSystemVersion { get; set; }
    public string? Description { get; set; }
    public string DistinguishedName { get; set; } = string.Empty;
    public DateTime? LastLogon { get; set; }
    public DateTime? WhenChanged { get; set; }
    public bool Enabled { get; set; } = true;

    public static CachedComputer From(ComputerInfo info) => new()
    {
        Id = info.Id,
        Name = info.Name,
        DnsName = info.DnsName,
        OperatingSystem = info.OperatingSystem,
        OperatingSystemVersion = info.OperatingSystemVersion,
        Description = info.Description,
        DistinguishedName = info.DistinguishedName,
        LastLogon = info.LastLogon,
        WhenChanged = info.WhenChanged,
        Enabled = info.Enabled,
    };

    public ComputerInfo ToInfo() => new()
    {
        Id = Id,
        Name = Name,
        DnsName = DnsName,
        OperatingSystem = OperatingSystem,
        OperatingSystemVersion = OperatingSystemVersion,
        Description = Description,
        DistinguishedName = DistinguishedName,
        LastLogon = LastLogon,
        WhenChanged = WhenChanged,
        Enabled = Enabled,
    };

    /// <summary>Запись без имени или DN повреждена — такой компьютер в индекс не берём.</summary>
    public bool IsValid => Id != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(DistinguishedName);
}
