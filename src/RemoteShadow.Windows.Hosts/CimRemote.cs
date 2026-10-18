using System.Security;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Результат StdRegProv.GetDWORDValue, разобранный по коду возврата.</summary>
internal enum RegistryReadOutcome
{
    /// <summary>Значение прочитано.</summary>
    Value,

    /// <summary>Ключа или значения нет.</summary>
    NotFound,

    AccessDenied,
    Error,
}

/// <summary>Чистые вспомогательные функции для CIM-запросов.</summary>
internal static class CimHelpers
{
    // ERROR_FILE_NOT_FOUND и ERROR_PATH_NOT_FOUND: StdRegProv возвращает их, когда нет ключа или значения.
    private const uint FileNotFound = 2;
    private const uint PathNotFound = 3;
    private const uint AccessDenied = 5;

    public static RegistryReadOutcome ClassifyRegistryReturn(uint returnValue) => returnValue switch
    {
        0 => RegistryReadOutcome.Value,
        FileNotFound or PathNotFound => RegistryReadOutcome.NotFound,
        AccessDenied => RegistryReadOutcome.AccessDenied,
        _ => RegistryReadOutcome.Error,
    };

    /// <summary>Экранирует строку для литерала WQL в одинарных кавычках: обратная косая черта и апостроф.</summary>
    public static string WqlEscape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>Нет пространства имён, класса, метода или запрос не поддерживается: компонент на компьютере отсутствует.</summary>
    public static bool IsMissingComponent(NativeErrorCode code) => code is
        NativeErrorCode.InvalidNamespace or NativeErrorCode.InvalidClass or NativeErrorCode.NotFound
        or NativeErrorCode.NotSupported or NativeErrorCode.MethodNotFound or NativeErrorCode.MethodNotAvailable
        or NativeErrorCode.InvalidQuery;

    /// <summary>Первая строка сообщения исключения, не длиннее <paramref name="maxChars"/>: CIM присылает длинные многострочные тексты.</summary>
    public static string ShortMessage(string? message, int maxChars = 200)
    {
        if (string.IsNullOrWhiteSpace(message)) return "причина не указана";
        var line = message.Trim().Split('\n', 2)[0].Trim();
        return WtsConversions.Truncate(line, maxChars);
    }
}

/// <summary>
/// Настоящая CIM-сессия по WinRM (Microsoft.Management.Infrastructure). Все вызовы синхронные;
/// ошибки превращаются в <see cref="CimRemoteException"/> с русским текстом.
/// </summary>
internal sealed class CimRemote : ICimRemote
{
    private const string Cimv2 = @"root\cimv2";
    private const string DefaultNamespace = @"root\default";
    private const string StandardCimv2 = @"root\StandardCimv2";
    private const uint HklmKey = 0x80000002;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);

    private readonly CimSession _session;

    // StdRegProv по документации живёт в root\default, по заданию его ищем в root\cimv2: берём первое пространство, где класс есть.
    private string? _registryNamespace;

    private CimRemote(CimSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Открывает сессию WSMan. С явными учётными данными — Negotiate с логином и паролем, иначе — учётные данные текущего
    /// пользователя. Соединение устанавливается при первом запросе, поэтому недоступный хост проявится там.
    /// </summary>
    public static CimRemote Connect(string host, AccountCredential? credential)
    {
        // SecureString здесь только формат аргумента CimCredential; освобождаем её после создания сессии.
        SecureString? secret = null;
        try
        {
            using var options = new WSManSessionOptions { Timeout = OperationTimeout };
            if (credential is not null)
            {
                secret = new SecureString();
                foreach (var c in credential.Password) secret.AppendChar(c);
                secret.MakeReadOnly();
                options.AddDestinationCredentials(
                    new CimCredential(PasswordAuthenticationMechanism.Negotiate, credential.Domain, credential.UserName, secret));
            }

            return new CimRemote(CimSession.Create(host, options));
        }
        catch (CimException ex)
        {
            throw Translate(ex);
        }
        finally
        {
            secret?.Dispose();
        }
    }

    public ServiceState? GetService(string serviceName)
    {
        using var instance = FindService(serviceName);
        if (instance is null) return null;

        return new ServiceState(
            instance.CimInstanceProperties["State"]?.Value as string ?? string.Empty,
            instance.CimInstanceProperties["StartMode"]?.Value as string ?? string.Empty);
    }

    public uint StartService(string serviceName)
    {
        try
        {
            using var instance = FindService(serviceName)
                ?? throw new CimRemoteException(CimFailureKind.NotAvailable, $"служба {serviceName} не найдена");
            using var options = NewOptions();
            using var parameters = new CimMethodParametersCollection();
            using var result = _session.InvokeMethod(Cimv2, instance, "StartService", parameters, options);
            return Convert.ToUInt32(result.ReturnValue.Value);
        }
        catch (CimException ex)
        {
            throw Translate(ex);
        }
    }

    public uint? ReadHklmDword(string subKey, string valueName)
    {
        try
        {
            var (outcome, returnValue, value) = InvokeGetDwordInAnyNamespace(subKey, valueName);
            return outcome switch
            {
                RegistryReadOutcome.Value => value,
                RegistryReadOutcome.NotFound => null,
                RegistryReadOutcome.AccessDenied =>
                    throw new CimRemoteException(CimFailureKind.AccessDenied, "нет прав на чтение реестра компьютера (код 5)"),
                _ => throw new CimRemoteException(CimFailureKind.Other, $"не удалось прочитать реестр компьютера (код {returnValue})"),
            };
        }
        catch (CimException ex)
        {
            throw Translate(ex);
        }
    }

    public bool? IsFirewallRuleEnabled(string ruleName, string displayName)
    {
        try
        {
            var byName = QueryRuleEnabled($"SELECT * FROM MSFT_NetFirewallRule WHERE Name = '{CimHelpers.WqlEscape(ruleName)}'");
            return byName ?? QueryRuleEnabled($"SELECT * FROM MSFT_NetFirewallRule WHERE DisplayName = '{CimHelpers.WqlEscape(displayName)}'");
        }
        catch (CimException ex)
        {
            throw Translate(ex);
        }
    }

    public void Dispose() => _session.Dispose();

    private (RegistryReadOutcome Outcome, uint ReturnValue, uint? Value) InvokeGetDwordInAnyNamespace(string subKey, string valueName)
    {
        if (_registryNamespace is { } known) return InvokeGetDword(known, subKey, valueName);

        CimException? firstMissing = null;
        foreach (var ns in new[] { Cimv2, DefaultNamespace })
        {
            try
            {
                var result = InvokeGetDword(ns, subKey, valueName);
                _registryNamespace = ns;
                return result;
            }
            catch (CimException ex) when (CimHelpers.IsMissingComponent(ex.NativeErrorCode))
            {
                firstMissing ??= ex;
            }
        }

        throw firstMissing!;
    }

    private (RegistryReadOutcome Outcome, uint ReturnValue, uint? Value) InvokeGetDword(string ns, string subKey, string valueName)
    {
        using var options = NewOptions();
        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmKey, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", subKey, CimType.String, CimFlags.In),
            CimMethodParameter.Create("sValueName", valueName, CimType.String, CimFlags.In),
        };
        using var result = _session.InvokeMethod(ns, "StdRegProv", "GetDWORDValue", parameters, options);

        var returnValue = Convert.ToUInt32(result.ReturnValue.Value);
        var outcome = CimHelpers.ClassifyRegistryReturn(returnValue);
        uint? value = null;
        if (outcome == RegistryReadOutcome.Value)
        {
            var raw = result.OutParameters.FirstOrDefault(p => p.Name == "uValue")?.Value;
            if (raw is null) outcome = RegistryReadOutcome.NotFound;
            else value = Convert.ToUInt32(raw);
        }

        return (outcome, returnValue, value);
    }

    private CimInstance? FindService(string serviceName)
    {
        using var options = NewOptions();
        var query = $"SELECT * FROM Win32_Service WHERE Name = '{CimHelpers.WqlEscape(serviceName)}'";
        CimInstance? found = null;
        foreach (var instance in _session.QueryInstances(Cimv2, "WQL", query, options))
        {
            if (found is null) found = instance;
            else instance.Dispose();
        }

        return found;
    }

    private bool? QueryRuleEnabled(string query)
    {
        using var options = NewOptions();
        // Эффективные правила (в том числе из GPO) лежат в ActiveStore; mustComply = false — сервер вправе проигнорировать опцию.
        options.SetCustomOption("PolicyStore", "ActiveStore", false);

        bool? enabled = null;
        foreach (var instance in _session.QueryInstances(StandardCimv2, "WQL", query, options))
        {
            using (instance)
            {
                var flag = Convert.ToInt32(instance.CimInstanceProperties["Enabled"]?.Value ?? 0);
                enabled = (enabled ?? false) || flag == 1;
            }
        }

        return enabled;
    }

    private static CimOperationOptions NewOptions() => new() { Timeout = OperationTimeout };

    private static CimRemoteException Translate(CimException ex)
    {
        var detail = CimHelpers.ShortMessage(ex.Message);
        if (CimHelpers.IsMissingComponent(ex.NativeErrorCode))
            return new CimRemoteException(CimFailureKind.NotAvailable, $"на компьютере нет нужного компонента управления ({detail})", ex);

        return ex.NativeErrorCode switch
        {
            NativeErrorCode.AccessDenied =>
                new CimRemoteException(CimFailureKind.AccessDenied, "нет прав для удалённого управления через WinRM (доступ запрещён)", ex),
            NativeErrorCode.Failed =>
                new CimRemoteException(CimFailureKind.Connection, $"не удалось подключиться по WinRM: {detail}", ex),
            _ => new CimRemoteException(CimFailureKind.Other, $"ошибка CIM: {detail}", ex),
        };
    }
}
