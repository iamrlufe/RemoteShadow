using RemoteShadow.Core.Models;

namespace RemoteShadow.Core.Services;

/// <summary>
/// Все компьютеры домена в памяти. LDAP используется только для обновления, а поиск, фильтры и списки
/// подразделений работают по памяти. Реализация — в RemoteShadow.Modules.
/// </summary>
public interface IComputerIndex
{
    IReadOnlyList<ComputerInfo> All { get; }
    bool IsLoading { get; }
    DateTime? LastSyncUtc { get; }
    string? LastError { get; }

    /// <summary>Индекс изменился (загрузка страницы, обновление, статус «в сети»). Событие может прийти из любого потока.</summary>
    event EventHandler? Changed;

    /// <summary>Читает кэш с диска, затем запускает фоновое обновление и таймеры (5 мин частичное, час полное).</summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>Загрузка из AD. full=false — только изменения по whenChanged; после смены контроллера домена — всегда полная.</summary>
    Task RefreshAsync(bool full, CancellationToken ct);

    /// <summary>Поиск без учёта регистра. Порядок: точное имя → начало имени → подстрока имени → подстрока описания/DNS-имени.</summary>
    IReadOnlyList<ComputerInfo> Search(string text, int max = 50);

    /// <summary>Компьютеры подразделения. includeNested — с вложенными (совпадение по суффиксу DN).</summary>
    IReadOnlyList<ComputerInfo> GetByOu(string ouDn, bool includeNested);

    ComputerInfo? Find(Guid id);

    /// <summary>Подставляет результат проверки «в сети». Вызывает <see cref="IOnlineStatusService"/>.</summary>
    void SetOnline(Guid id, bool? online);

    /// <summary>Сбрасывает данные (при смене учётной записи или домена).</summary>
    void Clear();
}

/// <summary>Статус «в сети» для видимых компьютеров: TCP 445 за 500 мс, не больше 64 одновременно, кэш на 2 минуты.</summary>
public interface IOnlineStatusService
{
    /// <summary>Проверяет компьютеры (только видимые и выбранное подразделение), обновляет индекс пачками.</summary>
    Task RefreshAsync(IReadOnlyCollection<ComputerInfo> computers, bool force, CancellationToken ct);
}

/// <summary>Кэш последних результатов опроса сессий, по компьютеру, с временем получения.</summary>
public interface ISessionCache
{
    SessionSnapshot? Get(Guid computerId);
    void Set(SessionSnapshot snapshot);
    IReadOnlyList<SessionSnapshot> GetAll();
    void Clear();
    event EventHandler? Changed;
}

/// <summary>Поиск «где сейчас пользователь».</summary>
public interface IUserLocator
{
    /// <summary>Совпадения в кэше сессий (по логину и ФИО из AD, если оно известно), без обращения в сеть.</summary>
    IReadOnlyList<UserLocation> FindCached(string text);

    /// <summary>
    /// Опрашивает компьютеры по мере ответов и отдаёт найденные сессии. scopeOuDn=null — весь домен
    /// (вызывать только по явной кнопке с предупреждением про EDR). Сначала проверяется порт 445.
    /// </summary>
    IAsyncEnumerable<UserLocation> FindAsync(string text, string? scopeOuDn, bool includeNestedOu, CancellationToken ct);
}
