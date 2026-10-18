using System.DirectoryServices.Protocols;

namespace RemoteShadow.Windows;

/// <summary>Атрибуты одной записи каталога. Интерфейс нужен, чтобы разбор записей проверялся тестами без LDAP.</summary>
internal interface ILdapAttributes
{
    /// <summary>Первое значение как строка или null, если атрибута нет.</summary>
    string? GetString(string name);

    /// <summary>Первое значение как байты (objectGUID, objectSid) или null.</summary>
    byte[]? GetBytes(string name);

    /// <summary>Все значения как строки; пустой список, если атрибута нет.</summary>
    IReadOnlyList<string> GetStrings(string name);
}

/// <summary>Обёртка над <see cref="SearchResultEntry"/>. Имена атрибутов сравниваются без учёта регистра.</summary>
internal sealed class SearchEntryAttributes : ILdapAttributes
{
    private readonly SearchResultEntry _entry;

    public SearchEntryAttributes(SearchResultEntry entry) => _entry = entry;

    public string DistinguishedName => _entry.DistinguishedName;

    public string? GetString(string name) => GetStrings(name) is { Count: > 0 } values ? values[0] : null;

    public byte[]? GetBytes(string name)
    {
        var attribute = Find(name);
        if (attribute is null) return null;
        var values = attribute.GetValues(typeof(byte[]));
        return values.Length > 0 ? values[0] as byte[] : null;
    }

    public IReadOnlyList<string> GetStrings(string name)
    {
        var attribute = Find(name);
        if (attribute is null) return Array.Empty<string>();
        return attribute.GetValues(typeof(string)).OfType<string>().ToArray();
    }

    private DirectoryAttribute? Find(string name) =>
        _entry.Attributes.Contains(name) ? _entry.Attributes[name] : null;
}
