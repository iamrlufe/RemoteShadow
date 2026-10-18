namespace RemoteShadow.Infrastructure;

internal static class Utf8Bom
{
    private static ReadOnlySpan<byte> Mark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Содержимое без метки порядка байтов UTF-8 в начале. Блокнот и PowerShell 5 добавляют её при сохранении файла,
    /// а <c>System.Text.Json</c> на готовых байтах её не принимает: файл, правленый руками, считался бы повреждённым.
    /// </summary>
    public static ReadOnlySpan<byte> Skip(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Mark) ? bytes.AsSpan(Mark.Length) : bytes;
}
