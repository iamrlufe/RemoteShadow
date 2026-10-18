using System.Globalization;
using RemoteShadow.Core;
using RemoteShadow.Windows.Native;

namespace RemoteShadow.Windows;

/// <summary>Сопоставление кодов ошибок Win32 при запуске процесса с понятными сообщениями.</summary>
internal static class ProcessLaunchErrors
{
    /// <param name="code">Код GetLastError / Win32Exception.NativeErrorCode.</param>
    /// <param name="program">Имя программы для сообщения.</param>
    /// <param name="systemMessage">Текст ошибки от Windows (Win32Exception.Message) для неизвестных кодов.</param>
    public static OperationFailedException FromWin32(int code, string program, string? systemMessage, Exception? inner = null)
    {
        var suffix = $"(код {code.ToString(CultureInfo.InvariantCulture)})";

        switch (code)
        {
            case Win32Errors.ServiceDisabled:
            case Win32Errors.ServiceNotActive:
            case Win32Errors.ServiceDoesNotExist:
                return new OperationFailedException(
                    "Не запущена служба «Вторичный вход в систему» (seclogon), поэтому программу нельзя открыть под другой учётной записью. "
                    + "Запустите службу: services.msc, «Вторичный вход в систему», «Запустить» (тип запуска «Вручную»), и повторите. "
                    + suffix,
                    OperationFailure.SecondaryLogonUnavailable,
                    inner);

            case Win32Errors.FileNotFound:
            case Win32Errors.PathNotFound:
                return new OperationFailedException(
                    $"Не найдена программа «{program}». Проверьте, что она установлена. {suffix}",
                    OperationFailure.NotFound,
                    inner);

            case Win32Errors.AccessDenied:
                return new OperationFailedException(
                    $"Отказано в доступе при запуске «{program}». Возможно, запуск заблокирован политикой или антивирусом. {suffix}",
                    OperationFailure.AccessDenied,
                    inner);

            case Win32Errors.LogonFailure:
            case Win32Errors.AccountRestriction:
            case Win32Errors.AccountDisabled:
            case Win32Errors.AccountLockedOut:
                return new OperationFailedException(
                    $"Не удалось запустить «{program}» под выбранной учётной записью: Windows отклонила её (неверный пароль, учётная запись отключена или заблокирована). {suffix}",
                    OperationFailure.AccessDenied,
                    inner);

            case Win32Errors.ElevationRequired:
                return new OperationFailedException(
                    $"Программа «{program}» требует повышения прав. Запустите RemoteShadow от имени администратора. {suffix}",
                    OperationFailure.AccessDenied,
                    inner);

            default:
                var detail = string.IsNullOrWhiteSpace(systemMessage) ? "причина неизвестна" : systemMessage.Trim().TrimEnd('.');
                return new OperationFailedException(
                    $"Не удалось запустить «{program}»: {detail}. {suffix}",
                    OperationFailure.Other,
                    inner);
        }
    }
}
