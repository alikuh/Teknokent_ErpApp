using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text.Json;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;

    public UsersController(AppDbContext context, IConnectionMultiplexer redis)
    {
        _context = context;
        _redis = redis;
    }

    public class RegisterRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    // POST: api/users/register
    [HttpPost("register")]
    public async Task<ActionResult> Register(RegisterRequest request)
    {
        var existingUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username);

        if (existingUser != null)
        {
            return BadRequest("Bu kullanıcı adı zaten alınmış.");
        }

        var salt = PasswordHasher.GenerateSalt();
        var hash = PasswordHasher.HashPassword(request.Password, salt);

        var user = new User
        {
            Username = request.Username,
            PasswordHash = hash,
            PasswordSalt = salt,
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return Ok(new { user.Id, user.Username });
    }

   // POST: api/users/login
    [HttpPost("login")]
    public async Task<ActionResult> Login(LoginRequest request)
    {
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user == null)
        {
            return Unauthorized("Kullanıcı adı veya şifre hatalı.");
        }

        bool isValid = PasswordHasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt);

        if (!isValid)
        {
            return Unauthorized("Kullanıcı adı veya şifre hatalı.");
        }

        string sessionToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        long absoluteExpiry = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();

        var db = _redis.GetDatabase();

        var hashEntries = new HashEntry[] 
        {
            new HashEntry("UserId", user.Id),
            new HashEntry("Username", user.Username),
            new HashEntry("AbsoluteExpiry", absoluteExpiry)
        };
        await db.HashSetAsync($"session:{sessionToken}", hashEntries);

        await db.KeyExpireAsync($"session:{sessionToken}", TimeSpan.FromMinutes(5));

        return Ok(new { token = sessionToken, username = user.Username });
    }
    
    // POST: api/users/logout
    [HttpPost("logout")]
    public async Task<ActionResult> Logout([FromHeader(Name = "Authorization")] string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Unauthorized("Token bulunamadı.");
        }

        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync($"session:{token}");

        return Ok(new { message = "Çıkış yapıldı." });
    }
}