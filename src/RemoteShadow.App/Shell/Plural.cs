namespace RemoteShadow.App.Shell;

/// <summary>Русские формы слова после числа: 1 компьютер, 2 компьютера, 5 компьютеров, 11 компьютеров, 21 компьютер.</summary>
public static class Plural
{
    public static string Choose(long count, string one, string few, string many)
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

    /// <summary>«5 компьютеров».</summary>
    public static string Format(long count, string one, string few, string many) => $"{count} {Choose(count, one, few, many)}";

    public static string Computers(long count) => Format(count, "компьютер", "компьютера", "компьютеров");
}
