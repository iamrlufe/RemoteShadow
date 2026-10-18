using System.Runtime.CompilerServices;

namespace RemoteShadow.Infrastructure;

/// <summary>Читает строки файла с конца, блоками. Нужен, чтобы последние записи журнала не требовали чтения всего файла.</summary>
internal static class ReverseLineReader
{
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    /// <summary>
    /// Строки (байты UTF-8, без перевода строки) от последней к первой; пустые пропускаются.
    /// Делить по байту 0x0A безопасно: в многобайтовых символах UTF-8 такого байта нет.
    /// </summary>
    public static async IAsyncEnumerable<byte[]> ReadLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default,
        int chunkSize = 64 * 1024)
    {
        var position = stream.Length;
        var buffer = new byte[chunkSize];

        // Хвост строки, начало которой ещё в предыдущем (по файлу) блоке.
        var tail = Array.Empty<byte>();

        while (position > 0)
        {
            ct.ThrowIfCancellationRequested();

            var size = (int)Math.Min(chunkSize, position);
            position -= size;
            stream.Seek(position, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, size), ct).ConfigureAwait(false);

            var lineEnd = size;
            for (var i = size - 1; i >= 0; i--)
            {
                if (buffer[i] != LineFeed) continue;

                var line = Join(buffer, i + 1, lineEnd, tail);
                tail = Array.Empty<byte>();
                lineEnd = i;

                if (line.Length > 0) yield return line;
            }

            tail = Join(buffer, 0, lineEnd, tail);
        }

        if (tail.Length > 0) yield return tail;
    }

    private static byte[] Join(byte[] buffer, int start, int end, byte[] tail)
    {
        var length = end - start;
        if (length == 0 && tail.Length == 0) return tail;

        var joined = new byte[length + tail.Length];
        Buffer.BlockCopy(buffer, start, joined, 0, length);
        Buffer.BlockCopy(tail, 0, joined, length, tail.Length);

        // Пустой хвост значит, что конец куска — настоящий конец строки, и перед ним может стоять символ CR.
        return tail.Length == 0 ? Trim(joined) : joined;
    }

    private static byte[] Trim(byte[] line) =>
        line.Length > 0 && line[^1] == CarriageReturn ? line[..^1] : line;
}
