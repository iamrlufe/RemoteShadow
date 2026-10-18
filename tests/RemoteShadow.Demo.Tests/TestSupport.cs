using Microsoft.Extensions.DependencyInjection;
using RemoteShadow.Core.Models;
using RemoteShadow.Demo;

namespace RemoteShadow.Demo.Tests;

internal static class TestSupport
{
    /// <summary>Фиксированное «сейчас» для воспроизводимых данных.</summary>
    public static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    public static DemoData Data(int computers = 230, int seed = 1) => DemoData.Generate(computers, seed, Now);

    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false)) list.Add(item);
        return list;
    }

    public static bool IsServer(ComputerInfo computer) =>
        computer.OperatingSystem?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Включённая рабочая станция «в сети», где есть сессия в нужном состоянии и нет «нет прав».</summary>
    public static ComputerInfo OnlineWorkstationWith(DemoData data, SessionState state) =>
        data.GetComputers().First(c =>
            !IsServer(c) && data.IsOnline(c.Id) && !data.IsAccessDenied(c.Id)
            && data.GetSessions(c.Id).Any(s => s.State == state && s.HasUser));

    public static ComputerInfo Offline(DemoData data) =>
        data.GetComputers().First(c => !data.IsOnline(c.Id));

    public static ComputerInfo OnlineWorkstation(DemoData data) =>
        data.GetComputers().First(c => !IsServer(c) && data.IsOnline(c.Id) && !data.IsAccessDenied(c.Id));

    /// <summary>Ждёт условие, проверяя его каждые 20 мс; false — не дождались.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(20);
        }

        return true;
    }
}

/// <summary>Управляемые часы для проверки whenChanged.</summary>
internal sealed class FakeClock
{
    public FakeClock(DateTime start) => Value = start;

    public DateTime Value { get; set; }

    public DateTime Read() => Value;
}

/// <summary>Минимальная коллекция сервисов без пакета Microsoft.Extensions.DependencyInjection.</summary>
internal sealed class TestServiceCollection : List<ServiceDescriptor>, IServiceCollection
{
}

/// <summary>
/// Минимальный провайдер для проверки регистрации: все сервисы Demo зарегистрированы фабриками или экземплярами.
/// Singleton'ы кэшируются.
/// </summary>
internal sealed class TestServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, ServiceDescriptor> _descriptors = new();
    private readonly Dictionary<Type, object> _instances = new();

    public TestServiceProvider(IEnumerable<ServiceDescriptor> descriptors)
    {
        foreach (var d in descriptors) _descriptors[d.ServiceType] = d;
    }

    public object? GetService(Type serviceType)
    {
        lock (_instances)
        {
            if (_instances.TryGetValue(serviceType, out var cached)) return cached;
            if (!_descriptors.TryGetValue(serviceType, out var d)) return null;

            var instance = d.ImplementationInstance
                           ?? d.ImplementationFactory?.Invoke(this)
                           ?? throw new InvalidOperationException($"Регистрация {serviceType.Name} без фабрики.");
            _instances[serviceType] = instance;
            return instance;
        }
    }
}
