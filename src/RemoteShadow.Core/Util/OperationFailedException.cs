namespace RemoteShadow.Core;

public enum OperationFailure
{
    Other,
    NotLoggedIn,
    AccessDenied,
    NotFound,
    Unreachable,
    Timeout,
    InvalidInput,

    /// <summary>Политика или конфигурация запрещает операцию (например, пароль не прошёл политику сложности).</summary>
    PolicyViolation,

    /// <summary>Не запущена служба «Вторичный вход в систему» (seclogon).</summary>
    SecondaryLogonUnavailable,
}

/// <summary>
/// Ожидаемая ошибка операции. Текст <see cref="Exception.Message"/> показывается пользователю как есть:
/// он должен быть понятным и на русском, без сырых кодов (код — в конце, в скобках).
/// </summary>
public sealed class OperationFailedException : Exception
{
    public OperationFailedException(string message, OperationFailure failure = OperationFailure.Other, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    public OperationFailure Failure { get; }
}
