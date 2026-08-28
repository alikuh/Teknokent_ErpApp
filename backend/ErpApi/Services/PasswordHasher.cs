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

    // OWASP'ın Argon2id için önerdiği taban yapılandırma: m=19 MiB, t=2, p=1.
    // Önceki değerler (m=64 MiB, t=4, p=8) her login'de ~500ms-1sn CPU harcıyordu;
    // özellikle p=8, çekirdek sayısı az makinelerde (WSL) hızlandırmak yerine
    // thread çekişmesi yaratıp yavaşlatıyordu. Bu değerler hâlâ güvenli aralıkta.
    // NOT: hash ham byte olarak saklandığı ve parametreleri içermediği için bu
    // değerleri değiştirmek eski hash'leri geçersiz kılar (kullanıcılar yeniden
    // kayıt olmalı).
    private const int Argon2DegreeOfParallelism = 1;
    private const int Argon2MemorySizeKib = 19456;
    private const int Argon2Iterations = 2;

    public static string HashPassword(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = Argon2DegreeOfParallelism,
            MemorySize = Argon2MemorySizeKib,
            Iterations = Argon2Iterations
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