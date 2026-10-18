using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Shell.Login;

/// <summary>Заголовки и подсказки к ошибкам входа. Сам текст ошибки приходит от сервиса входа в <c>LogonResult.Message</c>.</summary>
public static class LoginErrorTexts
{
    public static (string Title, string? Hint) Describe(LogonError error) => error switch
    {
        LogonError.InvalidCredentials => (
            "Неверный логин или пароль",
            "Проверьте раскладку клавиатуры и Caps Lock. Логин можно указать как CORP\\ivanov или ivanov@corp.local."),
        LogonError.AccountLocked => (
            "Учётная запись заблокирована",
            "Подождите окончания блокировки или обратитесь к администратору домена."),
        LogonError.AccountDisabled => (
            "Учётная запись отключена",
            "Обратитесь к администратору домена."),
        LogonError.PasswordExpired => (
            "Срок действия пароля истёк",
            "Смените пароль на доменном компьютере или через портал самообслуживания и войдите снова."),
        LogonError.DomainControllerUnreachable => (
            "Контроллер домена недоступен",
            "Проверьте сеть или VPN и доступность портов 389 и 636. Контроллер домена можно указать вручную."),
        LogonError.DnsFailure => (
            "Не удалось найти домен",
            "Проверьте имя домена. DNS-сервер компьютера должен разрешать имена домена (доменный DNS или VPN)."),
        LogonError.DomainNotSpecified => (
            "Не указан домен",
            "Введите DNS-имя домена, например corp.local."),
        _ => (
            "Не удалось войти",
            null),
    };
}
