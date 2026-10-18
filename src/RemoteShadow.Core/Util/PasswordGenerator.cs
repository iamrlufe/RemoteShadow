using System.Security.Cryptography;

namespace RemoteShadow.Core.Util;

public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*-_=+?";

    /// <summary>Криптостойкий пароль, проходящий типовую политику сложности (все четыре класса символов). Без похожих символов 0/O, 1/l/I.</summary>
    public static string Generate(int length = 14)
    {
        if (length < 8) throw new ArgumentOutOfRangeException(nameof(length), "Минимум 8 символов.");

        var all = Upper + Lower + Digits + Symbols;
        var chars = new char[length];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (var i = 4; i < length; i++) chars[i] = Pick(all);

        // Перемешивание Фишера — Йетса, чтобы обязательные символы не стояли в начале.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
