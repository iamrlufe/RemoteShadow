namespace RemoteShadow.Infrastructure;

internal static class RussianPlural
{
    /// <summary>Форма слова для числа: 1 объект, 2 объекта, 5 объектов, 11 объектов, 21 объект.</summary>
    public static string Choose(int count, string one, string few, string many)
    {
        var n = Math.Abs(count) % 100;
        if (n is >= 11 and <= 14) return many;

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}
