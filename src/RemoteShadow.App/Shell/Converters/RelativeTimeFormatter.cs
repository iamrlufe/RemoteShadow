using System.Globalization;

namespace RemoteShadow.App.Shell.Converters;

/// <summary>Время в виде «2 мин назад». Чистая функция: «сейчас» передаётся снаружи.</summary>
public static class RelativeTimeFormatter
{
    /// <summary>
    /// <paramref name="utc"/> — момент в UTC. Меньше минуты и будущее — «только что», дальше «N мин назад»,
    /// «N ч назад», «вчера», «N дн. назад», а старше месяца — дата.
    /// </summary>
    public static string Format(DateTime utc, DateTime nowUtc)
    {
        var delta = nowUtc - utc;
        if (delta < TimeSpan.FromMinutes(1)) return "только что";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} мин назад";
        if (delta < TimeSpan.FromHours(24)) return $"{(int)delta.TotalHours} ч назад";

        var days = (int)delta.TotalDays;
        if (days == 1) return "вчера";
        if (days < 30) return $"{days} дн. назад";
        return utc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>Приводит значение к UTC: неуказанный тип считается UTC (так в моделях Core).</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
