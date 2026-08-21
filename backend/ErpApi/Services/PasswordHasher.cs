using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using System.Text;

namespace ErpApi.Services;

public static class PasswordHasher
{
    public static byte[] GenerateSalt()
    {
        return RandomNumberGenerator.GetBytes(16);
    }

    public static string HashPassword(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 8,
            MemorySize = 65536,
            Iterations = 4
        };

        byte[] hashBytes = argon2.GetBytes(32);
        return Convert.ToBase64String(hashBytes);
    }

    public static bool VerifyPassword(string password, string storedHash, byte[] salt)
    {
        string hashOfInput = HashPassword(password, salt);

        byte[] inputBytes = Convert.FromBase64String(hashOfInput);
        byte[] storedBytes = Convert.FromBase64String(storedHash);

        return CryptographicOperations.FixedTimeEquals(inputBytes, storedBytes);
    }
}