using System.Text;

namespace RemoteShadow.Windows;

/// <summary>Кодирование значения unicodePwd: пароль в двойных кавычках, UTF-16LE, без BOM и завершающего нуля.</summary>
internal static class UnicodePwdEncoder
{
    /// <summary>
    /// Байты для атрибута unicodePwd. Строка с кавычками не создаётся, чтобы лишняя копия пароля не оставалась в памяти.
    /// Вызывающий обнуляет массив после отправки.
    /// </summary>
    public static byte[] Encode(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var bytes = new byte[(password.Length + 2) * 2];
        bytes[0] = (byte)'"';
        var written = Encoding.Unicode.GetBytes(password, 0, password.Length, bytes, 2);
        bytes[2 + written] = (byte)'"';
        return bytes;
    }
}
