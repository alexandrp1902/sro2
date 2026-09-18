using System.Security.Cryptography;
using System.Text;

namespace Sro.Server.Accounts;

/// <summary>
/// Пароли и ключи устройств. Пароль хранится как PBKDF2-SHA256 с солью на аккаунт; ключ устройства — как SHA-256:
/// он и так случайный и длинный, медленный хеш ему не нужен.
/// </summary>
internal static class Passwords
{
    /// <summary>Порядка 50–100 мс на вход: перебор по сети дорог, а пилоту незаметно.</summary>
    public const int DefaultIterations = 100_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int KeyBytes = 32;

    /// <returns>Строка вида <c>pbkdf2-sha256$итерации$соль$хеш</c> (base64).</returns>
    public static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <returns>false и для неверного пароля, и для испорченной строки хеша.</returns>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Новый ключ устройства: 64 шестнадцатеричных символа.</summary>
    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>В файле аккаунта лежит только хеш ключа: утёкший файл не пускает в игру.</summary>
    public static string KeyHash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
