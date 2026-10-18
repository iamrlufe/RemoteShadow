using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;

namespace RemoteShadow.App.Tests;

/// <summary>Минимальный <see cref="IComputerIndex"/> в памяти: без кэша на диске и без фонового обновления.</summary>
internal sealed class FakeComputerIndex : IComputerIndex
{
    private readonly List<ComputerInfo> _computers;

    public FakeComputerIndex(IEnumerable<ComputerInfo> computers) => _computers = computers.ToList();

    public IReadOnlyList<ComputerInfo> All => _computers;
    public bool IsLoading => false;
    public DateTime? LastSyncUtc => null;
    public string? LastError => null;

    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task RefreshAsync(bool full, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<ComputerInfo> Search(string text, int max = 50) =>
        _computers
            .Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (c.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(max)
            .ToList();

    public IReadOnlyList<ComputerInfo> GetByOu(string ouDn, bool includeNested) =>
        _computers.Where(c => string.Equals(c.ParentDn, ouDn, StringComparison.OrdinalIgnoreCase)).ToList();

    public ComputerInfo? Find(Guid id) => _computers.FirstOrDefault(c => c.Id == id);

    public void SetOnline(Guid id, bool? online)
    {
        var index = _computers.FindIndex(c => c.Id == id);
        if (index >= 0) _computers[index] = _computers[index] with { IsOnline = online };
    }

    public void Clear() => _computers.Clear();
}

/// <summary>Записывает последние уведомления для проверки в тестах, не трогает UI-поток.</summary>
internal sealed class FakeNotificationService : INotificationService
{
    public List<string> InfoMessages { get; } = new();
    public List<string> SuccessMessages { get; } = new();
    public List<string> ErrorMessages { get; } = new();

    public void Info(string message) => InfoMessages.Add(message);
    public void Success(string message) => SuccessMessages.Add(message);
    public void Error(string message) => ErrorMessages.Add(message);
}

/// <summary>Диалоги всегда подтверждают: нужны, чтобы <see cref="RemoteShadow.Infrastructure.ActionRunner"/> мог выполнять опасные действия без реального окна.</summary>
internal sealed class FakeDialogService : IDialogService
{
    public string ReasonText { get; set; } = "Причина для теста";
    public string PromptTextResult { get; set; } = "Текст для теста";
    public PasswordChoice PasswordResult { get; set; } = new("Passw0rd!1", true);

    public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);

    public Task<ReasonResult> ConfirmWithReasonAsync(ConfirmRequest request) =>
        Task.FromResult(new ReasonResult(true, ReasonText));

    public Task<string?> PromptTextAsync(string title, string message, string? initial = null, bool multiline = false) =>
        Task.FromResult<string?>(PromptTextResult);

    public Task<PasswordChoice?> PromptPasswordResetAsync(string userLabel) =>
        Task.FromResult<PasswordChoice?>(PasswordResult);

    public async Task<GroupInfo?> PickGroupAsync(string title, Func<string, CancellationToken, Task<IReadOnlyList<GroupInfo>>> search)
    {
        var found = await search(string.Empty, CancellationToken.None);
        return found.FirstOrDefault();
    }

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
}

/// <summary>Журнал в памяти — чтобы тесты не писали на диск.</summary>
internal sealed class FakeAuditLog : IAuditLog
{
    public List<AuditRecord> Records { get; } = new();

    public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int max, DateTime? sinceUtc = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>(Records.Take(max).ToList());
}

/// <summary>Настройки в памяти — чтобы тесты не писали на диск.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private AppSettings _settings = new();

    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) => _settings = settings;
}
