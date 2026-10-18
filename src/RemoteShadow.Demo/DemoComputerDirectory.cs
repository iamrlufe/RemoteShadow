using System.Runtime.CompilerServices;
using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo;

/// <summary>
/// Тестовый каталог компьютеров. Отдаёт данные так же, как LDAP-реализация: страницами по 500, с фильтрами
/// <see cref="ComputerQuery"/>. Методы Rename/MoveTo/Add/Remove/SwitchDomainController нужны тестам индекса:
/// они меняют каталог и обновляют whenChanged так же, как это делает AD.
/// </summary>
public sealed class DemoComputerDirectory : IComputerDirectory
{
    public const int PageSize = 500;

    private readonly DemoData _data;
    private readonly DemoTimings _timings;

    public DemoComputerDirectory(DemoData data, DemoTimings? timings = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _timings = timings ?? DemoTimings.Default;
    }

    /// <summary>Контроллер домена; по умолчанию DC01.corp.local.</summary>
    public string? DomainController => _data.DomainController;

    public Task<string> GetRootDnAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(DemoData.RootDn);
    }

    /// <summary>
    /// Записи отдаются в порядке DN. Перед каждой страницей небольшая пауза (<see cref="DemoTimings.DirectoryPageDelay"/>).
    /// Состояние снимается в начале перечисления: изменения во время загрузки видны при следующем обновлении.
    /// </summary>
    public async IAsyncEnumerable<ComputerInfo> EnumerateAsync(ComputerQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();

        DateTime? since = query.ChangedSince is { } s
            ? s.Kind == DateTimeKind.Local ? s.ToUniversalTime() : s
            : null;

        var snapshot = _data.GetComputers()
            .Where(c => (query.IncludeDisabled || c.Enabled) && (since is null || c.WhenChanged is null || c.WhenChanged >= since))
            .OrderBy(c => c.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var offset = 0; offset < snapshot.Count; offset += PageSize)
        {
            await DemoDelay.Wait(_timings.DirectoryPageDelay, ct).ConfigureAwait(false);

            var end = Math.Min(offset + PageSize, snapshot.Count);
            for (var i = offset; i < end; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return snapshot[i];
            }
        }
    }

    public Task<IReadOnlyList<OuNode>> GetOuChildrenAsync(string parentDn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(parentDn);

        var ous = _data.Ous;
        IReadOnlyList<OuNode> result = ous
            .Where(o => Dn.AreEqual(o.ParentDn, parentDn))
            .Select(o => new OuNode(o.DistinguishedName, o.Name, ous.Any(c => Dn.AreEqual(c.ParentDn, o.DistinguishedName))))
            .OrderBy(n => n.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>Переименовывает компьютер: меняются имя, DNS-имя и DN, objectGUID остаётся прежним; whenChanged обновляется.</summary>
    public ComputerInfo Rename(Guid id, string newName) => _data.RenameComputer(id, newName);

    /// <summary>Переносит компьютер в другое подразделение (оно должно существовать); whenChanged обновляется.</summary>
    public ComputerInfo MoveTo(Guid id, string newOuDn) => _data.MoveComputer(id, newOuDn);

    /// <summary>Создаёт компьютер с новым objectGUID и свежим whenChanged.</summary>
    public ComputerInfo Add(
        string name,
        string ouDn,
        string? description = null,
        string? operatingSystem = "Windows 11 Pro",
        bool online = true,
        bool enabled = true) =>
        _data.AddComputer(name, ouDn, description, operatingSystem, online, enabled);

    /// <summary>Удаляет компьютер. Частичное обновление (по whenChanged) этого не видит, как и в AD.</summary>
    public bool Remove(Guid id) => _data.RemoveComputer(id);

    /// <summary>Включает или отключает учётную запись компьютера; whenChanged обновляется.</summary>
    public ComputerInfo SetEnabled(Guid id, bool enabled) => _data.SetComputerEnabled(id, enabled);

    /// <summary>
    /// Меняет контроллер домена. whenChanged не реплицируется, поэтому по умолчанию у всех записей отметка
    /// становится новой: частичное обновление с другого контроллера нельзя сравнивать с прежним.
    /// </summary>
    public void SwitchDomainController(string name, bool bumpWhenChanged = true) =>
        _data.SwitchDomainController(name, bumpWhenChanged);
}
