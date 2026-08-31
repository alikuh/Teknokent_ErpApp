using ErpApi.Services;

namespace ErpApi.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void GenerateSalt_Returns16Bytes()
    {
        var salt = PasswordHasher.GenerateSalt();

        Assert.Equal(16, salt.Length);
    }

    [Fact]
    public void GenerateSalt_ReturnsDifferentValuesEachCall()
    {
        var a = PasswordHasher.GenerateSalt();
        var b = PasswordHasher.GenerateSalt();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashPassword_IsDeterministicForSamePasswordAndSalt()
    {
        var salt = PasswordHasher.GenerateSalt();

        var hash1 = PasswordHasher.HashPassword("correct horse battery staple", salt);
        var hash2 = PasswordHasher.HashPassword("correct horse battery staple", salt);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashPassword_DiffersWhenSaltDiffers()
    {
        var hashA = PasswordHasher.HashPassword("hunter2", PasswordHasher.GenerateSalt());
        var hashB = PasswordHasher.HashPassword("hunter2", PasswordHasher.GenerateSalt());

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void HashPassword_DiffersWhenPasswordDiffers()
    {
        var salt = PasswordHasher.GenerateSalt();

        var hashA = PasswordHasher.HashPassword("password-a", salt);
        var hashB = PasswordHasher.HashPassword("password-b", salt);

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void HashPassword_ProducesA32ByteBase64Hash()
    {
        var salt = PasswordHasher.GenerateSalt();

        var hash = PasswordHasher.HashPassword("whatever", salt);

        Assert.Equal(32, Convert.FromBase64String(hash).Length);
    }

    [Fact]
    public void VerifyPassword_ReturnsTrueForCorrectPassword()
    {
        var salt = PasswordHasher.GenerateSalt();
        var stored = PasswordHasher.HashPassword("s3cr3t!", salt);

        Assert.True(PasswordHasher.VerifyPassword("s3cr3t!", stored, salt));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForWrongPassword()
    {
        var salt = PasswordHasher.GenerateSalt();
        var stored = PasswordHasher.HashPassword("s3cr3t!", salt);

        Assert.False(PasswordHasher.VerifyPassword("not-the-password", stored, salt));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseWhenSaltDoesNotMatch()
    {
        var stored = PasswordHasher.HashPassword("s3cr3t!", PasswordHasher.GenerateSalt());

        Assert.False(PasswordHasher.VerifyPassword("s3cr3t!", stored, PasswordHasher.GenerateSalt()));
    }
}
