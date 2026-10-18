using RemoteShadow.Core.Models;
using RemoteShadow.Core.Services;
using RemoteShadow.Core.Util;

namespace RemoteShadow.Demo;

/// <summary>
/// Тестовый вход в домен. Явные данные: пароль «demo» подходит любому логину; «da-...» — привилегированная запись;
/// логины «locked», «disabled», «expired» и «unreachable» показывают соответствующие ошибки при любом пароле.
/// Домен должен быть тот, что задан в конструкторе (по умолчанию corp.local), иначе — ошибка DNS.
/// </summary>
public sealed class DemoDomainLogonService : IDomainLogonService
{
    public const string DemoPassword = "demo";
    public const string PrivilegedPrefix = "da-";

    private readonly bool _isDomainJoined;
    private readonly string _domainName;
    private readonly string _computerName;
    private readonly string _currentUser;
    private readonly DemoTimings _timings;

    /// <param name="isDomainJoined">Состоит ли «этот компьютер» в домене: от этого зависит, какое окно входа покажет программа.</param>
    /// <param name="domainName">Единственный домен, куда можно войти.</param>
    /// <param name="currentUser">Логин «пользователя Windows» для входа без пароля.</param>
    public DemoDomainLogonService(
        bool isDomainJoined = true,
        string domainName = DemoData.DomainName,
        string computerName = "DEMO-PC",
        string currentUser = "demo-admin",
        DemoTimings? timings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainName);
        _isDomainJoined = isDomainJoined;
        _domainName = domainName.Trim();
        _computerName = computerName;
        _currentUser = currentUser;
        _timings = timings ?? DemoTimings.Default;
    }

    public DomainEnvironmentInfo DetectEnvironment() =>
        new(_isDomainJoined, _isDomainJoined ? _domainName : null, _computerName, _currentUser);

    public async Task<LogonResult> LogonAsync(LogonRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await DemoDelay.Wait(_timings.LogonDelay, ct).ConfigureAwait(false);

        return request.Mode == LogonMode.CurrentUser ? LogonCurrentUser(request) : LogonExplicit(request);
    }

    private LogonResult LogonCurrentUser(LogonRequest request)
    {
        var login = AccountNameParser.Parse(_currentUser).UserName;
        var connection = new DemoDomainConnection(
            _domainName, ControllerFor(request), $"{NetBios(_domainName)}\\{login}", usesExplicitCredentials: false);
        return LogonResult.Ok(connection, IsPrivileged(login));
    }

    private LogonResult LogonExplicit(LogonRequest request)
    {
        var parsed = AccountNameParser.Parse(request.UserName ?? string.Empty);
        var login = parsed.UserName;

        // Домен может прийти из поля «Домен», из суффикса UPN и из CORP\логин: все указанные должны совпасть с демо-доменом.
        var wrong = new[] { request.Domain, parsed.UpnSuffix, parsed.Domain }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.Trim())
            .FirstOrDefault(d => !IsDemoDomain(d));
        if (wrong is not null)
        {
            return LogonResult.Fail(
                LogonError.DnsFailure,
                $"Не удалось найти домен «{wrong}» в DNS. Проверьте имя домена и DNS-сервер компьютера (9003).");
        }

        var domainGiven = !string.IsNullOrWhiteSpace(request.Domain) || parsed.UpnSuffix is not null || parsed.Domain is not null;
        if (!domainGiven && !_isDomainJoined)
            return LogonResult.Fail(LogonError.DomainNotSpecified, "Укажите домен, например corp.local.");

        if (string.IsNullOrWhiteSpace(login))
            return LogonResult.Fail(LogonError.InvalidCredentials, "Введите логин.");

        switch (login.ToLowerInvariant())
        {
            case "locked":
                return LogonResult.Fail(LogonError.AccountLocked,
                    "Учётная запись заблокирована. Подождите окончания блокировки или обратитесь к администратору домена (1909).");
            case "disabled":
                return LogonResult.Fail(LogonError.AccountDisabled, "Учётная запись отключена (1331).");
            case "expired":
                return LogonResult.Fail(LogonError.PasswordExpired, "Срок действия пароля истёк. Смените пароль и повторите вход (1330).");
            case "unreachable":
                return LogonResult.Fail(LogonError.DomainControllerUnreachable,
                    "Контроллер домена недоступен. Проверьте сеть, VPN и DNS (81).");
        }

        if (request.Password != DemoPassword)
            return LogonResult.Fail(LogonError.InvalidCredentials, "Неверное имя пользователя или пароль (1326).");

        var netBios = NetBios(_domainName);
        var connection = new DemoDomainConnection(
            _domainName,
            ControllerFor(request),
            $"{netBios}\\{login}",
            usesExplicitCredentials: true,
            new AccountCredential(netBios, login, request.Password));
        return LogonResult.Ok(connection, IsPrivileged(login));
    }

    private string ControllerFor(LogonRequest request) =>
        string.IsNullOrWhiteSpace(request.DomainController)
            ? $"DC01.{_domainName}"
            : request.DomainController.Trim();

    /// <summary>Подходит DNS-имя домена («corp.local») и NetBIOS-имя («CORP»).</summary>
    private bool IsDemoDomain(string name) =>
        string.Equals(name, _domainName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, NetBios(_domainName), StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivileged(string login) => login.StartsWith(PrivilegedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>corp.local → CORP.</summary>
    private static string NetBios(string dnsDomain) => dnsDomain.Split('.')[0].ToUpperInvariant();
}
