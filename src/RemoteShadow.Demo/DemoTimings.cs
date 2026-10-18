namespace RemoteShadow.Demo;

/// <summary>
/// Задержки тестовых сервисов: делают демо похожим на настоящую сеть. В тестах берут <see cref="Instant"/>.
/// </summary>
public sealed record DemoTimings
{
    /// <summary>Опрос сессий живого хоста: задержка выбирается по имени хоста из диапазона [Min, Max].</summary>
    public TimeSpan SessionQueryMin { get; init; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan SessionQueryMax { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Сколько ждём, прежде чем признать хост недоступным.</summary>
    public TimeSpan UnreachableDelay { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Сообщение, отключение, выход, перезагрузка.</summary>
    public TimeSpan ActionDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Через сколько компьютер после перезагрузки или выключения снова «в сети».</summary>
    public TimeSpan ShutdownReturnDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Проверка порта: задержка от 0 до этого значения, по имени хоста и порту.</summary>
    public TimeSpan ProbeMax { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Пауза перед каждой страницей (500 компьютеров) при загрузке каталога.</summary>
    public TimeSpan DirectoryPageDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Диагностика компьютера: задержка из диапазона [Min, Max] по имени.</summary>
    public TimeSpan DiagnosticsMin { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan DiagnosticsMax { get; init; } = TimeSpan.FromMilliseconds(900);

    /// <summary>Вход в домен.</summary>
    public TimeSpan LogonDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    public static DemoTimings Default { get; } = new();

    /// <summary>Без задержек (для тестов). Компьютер после выключения возвращается через 250 мс.</summary>
    public static DemoTimings Instant { get; } = new()
    {
        SessionQueryMin = TimeSpan.Zero,
        SessionQueryMax = TimeSpan.Zero,
        UnreachableDelay = TimeSpan.Zero,
        ActionDelay = TimeSpan.Zero,
        ShutdownReturnDelay = TimeSpan.FromMilliseconds(250),
        ProbeMax = TimeSpan.Zero,
        DirectoryPageDelay = TimeSpan.Zero,
        DiagnosticsMin = TimeSpan.Zero,
        DiagnosticsMax = TimeSpan.Zero,
        LogonDelay = TimeSpan.Zero,
    };
}
