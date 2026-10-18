using System.ComponentModel;
using RemoteShadow.Core;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Windows.Hosts;

/// <summary>Ошибка Win32 из вызова WTS или завершения работы. Текст для пользователя строит <see cref="WtsErrorMapper"/>.</summary>
internal sealed class WtsException : Exception
{
    public WtsException(string api, int errorCode)
        : base($"{api}: ошибка Win32 {errorCode}")
    {
        Api = api;
        ErrorCode = errorCode;
    }

    public string Api { get; }
    public int ErrorCode { get; }
}

/// <summary>Итог разбора кода ошибки: статус опроса, тип сбоя операции и готовый русский текст.</summary>
internal readonly record struct WtsFailure(HostQueryStatus Status, OperationFailure Failure, string Message);

/// <summary>Сопоставляет коды Win32 из WTS и InitiateSystemShutdownEx понятным сообщениям на русском.</summary>
internal static class WtsErrorMapper
{
    public const int AccessDenied = 5;
    public const int NetworkPathNotFound = 53;
    public const int NetworkNameNotFound = 67;
    public const int RemoteSystemRefused = 1225;
    public const int NetworkUnreachable = 1231;
    public const int ErrorTimeout = 1460;
    public const int WaitTimeout = 258;
    public const int RpcServerUnavailable = 1722;
    public const int ShutdownInProgress = 1115;

    /// <summary>Статус опроса хоста по коду ошибки.</summary>
    public static HostQueryStatus ToStatus(int error) => error switch
    {
        RpcServerUnavailable or NetworkPathNotFound or NetworkNameNotFound or NetworkUnreachable or RemoteSystemRefused
            => HostQueryStatus.Unreachable,
        AccessDenied => HostQueryStatus.AccessDenied,
        ErrorTimeout or WaitTimeout => HostQueryStatus.Timeout,
        _ => HostQueryStatus.Error,
    };

    /// <summary>Ошибка опроса сессий компьютера.</summary>
    public static WtsFailure MapQuery(int error, string host)
    {
        var status = ToStatus(error);
        var message = status switch
        {
            HostQueryStatus.Unreachable => $"Компьютер {host} недоступен: {UnreachableReason(error)} (код {error}).",
            HostQueryStatus.AccessDenied =>
                $"Нет прав администратора на компьютере {host}. Учётной записи нужны права локального администратора на этом компьютере (код {error}).",
            HostQueryStatus.Timeout => $"Компьютер {host} не отвечает: превышено время ожидания (код {error}).",
            _ => $"Не удалось получить сессии компьютера {host}: {SystemText(error)} (код {error}).",
        };
        return new WtsFailure(status, ToFailure(status), message);
    }

    /// <summary>
    /// Ошибка действия над сессией. <paramref name="operation"/> — глагол в неопределённой форме:
    /// «отправить сообщение в сессию 3», «завершить сессию 3».
    /// </summary>
    public static WtsFailure MapOperation(string operation, int error, string host)
    {
        var status = ToStatus(error);
        var reason = status switch
        {
            HostQueryStatus.Unreachable => $"компьютер недоступен — {UnreachableReason(error)}",
            HostQueryStatus.AccessDenied => "нет прав администратора на компьютере",
            HostQueryStatus.Timeout => "превышено время ожидания",
            _ => SystemText(error),
        };
        return new WtsFailure(status, ToFailure(status), $"Не удалось {operation} на компьютере {host}: {reason} (код {error}).");
    }

    /// <summary>Ошибка перезагрузки или выключения. Код 5 здесь означает нехватку права SeRemoteShutdownPrivilege.</summary>
    public static WtsFailure MapShutdown(string operation, int error, string host)
    {
        if (error == AccessDenied)
        {
            return new WtsFailure(
                HostQueryStatus.AccessDenied,
                OperationFailure.AccessDenied,
                $"Не удалось {operation} на компьютере {host}: нет права «Принудительное удалённое завершение работы» " +
                $"(SeRemoteShutdownPrivilege). Учётной записи нужно это право на целевом компьютере — обычно оно есть у администраторов (код {error}).");
        }

        if (error == ShutdownInProgress)
        {
            return new WtsFailure(
                HostQueryStatus.Error,
                OperationFailure.Other,
                $"Не удалось {operation} на компьютере {host}: завершение работы уже выполняется (код {error}).");
        }

        return MapOperation(operation, error, host);
    }

    /// <summary>Тип сбоя для <see cref="OperationFailedException"/>.</summary>
    public static OperationFailure ToFailure(HostQueryStatus status) => status switch
    {
        HostQueryStatus.Unreachable => OperationFailure.Unreachable,
        HostQueryStatus.AccessDenied => OperationFailure.AccessDenied,
        HostQueryStatus.Timeout => OperationFailure.Timeout,
        _ => OperationFailure.Other,
    };

    private static string UnreachableReason(int error) => error switch
    {
        RpcServerUnavailable => "RPC-сервер недоступен. Проверьте, что компьютер включён, имя разрешается в DNS, а порты 135 и 445 открыты; если всё так — запустите диагностику компьютера",
        NetworkPathNotFound => "сетевой путь не найден. Проверьте имя компьютера и DNS (для удалённой работы — VPN)",
        NetworkNameNotFound => "сетевое имя не найдено. Проверьте имя компьютера и DNS",
        NetworkUnreachable => "сеть недоступна. Проверьте подключение и VPN",
        RemoteSystemRefused => "компьютер отклонил подключение. Проверьте, что служба удалённого управления запущена и порт 445 не закрыт брандмауэром",
        _ => "нет связи",
    };

    private static string SystemText(int error)
    {
        var text = new Win32Exception(error).Message.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(text) ? "неизвестная ошибка" : text;
    }
}
