using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Состояние службы Windows: State («Running», «Stopped»…) и StartMode («Auto», «Manual», «Disabled»).</summary>
internal sealed record ServiceState(string State, string StartMode);

/// <summary>Почему CIM-проверку выполнить не удалось.</summary>
internal enum CimFailureKind
{
    /// <summary>Нет связи по WinRM или сбой транспорта: остальные проверки тоже не выполнятся.</summary>
    Connection,

    /// <summary>Учётной записи не хватает прав: остальные проверки тоже не выполнятся.</summary>
    AccessDenied,

    /// <summary>На компьютере нет нужного пространства имён, класса или метода (старая ОС, отключённый компонент).</summary>
    NotAvailable,

    Other,
}

/// <summary>Ошибка CIM-запроса с текстом на русском, пригодным для показа в отчёте.</summary>
internal sealed class CimRemoteException : Exception
{
    public CimRemoteException(CimFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public CimFailureKind Kind { get; }

    /// <summary>Фатальная ошибка делает бессмысленными остальные запросы к этому компьютеру.</summary>
    public bool IsFatal => Kind is CimFailureKind.Connection or CimFailureKind.AccessDenied;
}

/// <summary>
/// Блокирующие CIM-запросы к одному компьютеру. Один экземпляр = одна CIM-сессия; вызывать из одного потока.
/// Интерфейс нужен, чтобы тестировать порядок и обработку ошибок диагностики без WinRM.
/// </summary>
internal interface ICimRemote : IDisposable
{
    /// <summary>Win32_Service по имени; null, если службы нет.</summary>
    ServiceState? GetService(string serviceName);

    /// <summary>Win32_Service.StartService; возвращает код возврата метода (0 — успех).</summary>
    uint StartService(string serviceName);

    /// <summary>DWORD из HKLM через StdRegProv; null, если значения нет.</summary>
    uint? ReadHklmDword(string subKey, string valueName);

    /// <summary>
    /// Включено ли правило брандмауэра (MSFT_NetFirewallRule.Enabled = 1); null, если правило не найдено.
    /// Ищет по имени правила, затем по отображаемому имени.
    /// </summary>
    bool? IsFirewallRuleEnabled(string ruleName, string displayName);
}

/// <summary>Создаёт CIM-сессию к компьютеру. <paramref name="credential"/> = null — учётные данные текущего пользователя.</summary>
internal delegate ICimRemote CimRemoteFactory(string host, AccountCredential? credential);
