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
    private readonly ILogger<UsersController> _logger;
    private readonly IGeoLocationService _geoLocation;

    public UsersController(AppDbContext context, IConnectionMultiplexer redis, ILogger<UsersController> logger, IGeoLocationService geoLocation)
    {
        _context = context;
        _redis = redis;
        _logger = logger;
        _geoLocation = geoLocation;
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

        _logger.LogInformation("Yeni kullanıcı kaydı oluşturuldu: {Username} (Id: {UserId})", user.Username, user.Id);

        return Ok(new { user.Id, user.Username });
    }

    private const int MaxFailedAttemptsPerUser = 5;
    private const int MaxFailedAttemptsPerIp = 20;
    private static readonly TimeSpan FailedAttemptLockout = TimeSpan.FromMinutes(15);

    // POST: api/users/login
    [HttpPost("login")]
    public async Task<ActionResult> Login(LoginRequest request)
    {
        IDatabase db;
        string clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string failKey = $"failed:{request.Username}";
        string ipFailKey = $"failed:ip:{clientIp}";

        try
        {
            db = _redis.GetDatabase();

            var ipFailCount = await db.StringGetAsync(ipFailKey);
            int currentIpFailCount = ipFailCount.IsNullOrEmpty ? 0 : (int)ipFailCount;

            if (currentIpFailCount >= MaxFailedAttemptsPerIp)
            {
                var ipTtl = await db.KeyTimeToLiveAsync(ipFailKey);
                int ipRemainingMinutes = ipTtl.HasValue ? (int)Math.Ceiling(ipTtl.Value.TotalMinutes) : (int)FailedAttemptLockout.TotalMinutes;
                AppMetrics.FailedLoginLockoutsTotal.WithLabels("ip").Inc();
                _logger.LogWarning("IP kilitlendi, login reddedildi: {ClientIp} (kullanıcı adı denenen: {Username})", clientIp, request.Username);
                return StatusCode(429, $"Bu IP adresinden çok fazla başarısız deneme yapıldı. Lütfen {ipRemainingMinutes} dakika sonra tekrar deneyin.");
            }

            var failCount = await db.StringGetAsync(failKey);
            int currentFailCount = failCount.IsNullOrEmpty ? 0 : (int)failCount;

            if (currentFailCount >= MaxFailedAttemptsPerUser)
            {
                var ttl = await db.KeyTimeToLiveAsync(failKey);
                int remainingMinutes = ttl.HasValue ? (int)Math.Ceiling(ttl.Value.TotalMinutes) : (int)FailedAttemptLockout.TotalMinutes;
                AppMetrics.FailedLoginLockoutsTotal.WithLabels("user").Inc();
                _logger.LogWarning("Kullanıcı kilitlendi, login reddedildi: {Username} (IP: {ClientIp})", request.Username, clientIp);
                return StatusCode(429, $"Çok fazla başarısız deneme. Lütfen {remainingMinutes} dakika sonra tekrar deneyin.");
            }
        }
        catch (RedisException)
        {
            return StatusCode(503, "Servis şu anda kullanılamıyor. Lütfen daha sonra tekrar deneyin.");
        }

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username);

        try
        {
            if (user == null)
            {
                await RegisterFailedAttemptAsync(db, failKey);
                await RegisterFailedAttemptAsync(db, ipFailKey);
                AppMetrics.LoginAttemptsTotal.WithLabels("failure").Inc();
                _logger.LogWarning("Login başarısız (kullanıcı bulunamadı): {Username} (IP: {ClientIp})", request.Username, clientIp);
                return Unauthorized("Kullanıcı adı veya şifre hatalı.");
            }

            bool isValid = PasswordHasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt);

            if (!isValid)
            {
                await RegisterFailedAttemptAsync(db, failKey);
                await RegisterFailedAttemptAsync(db, ipFailKey);
                AppMetrics.LoginAttemptsTotal.WithLabels("failure").Inc();
                _logger.LogWarning("Login başarısız (şifre hatalı): {Username} (IP: {ClientIp})", request.Username, clientIp);
                return Unauthorized("Kullanıcı adı veya şifre hatalı.");
            }

            string sessionToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            long absoluteExpiry = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();

            var hashEntries = new HashEntry[]
            {
                new HashEntry("UserId", user.Id),
                new HashEntry("Username", user.Username),
                new HashEntry("AbsoluteExpiry", absoluteExpiry)
            };
            await db.HashSetAsync($"session:{sessionToken}", hashEntries);
            await db.KeyExpireAsync($"session:{sessionToken}", TimeSpan.FromMinutes(5));

            // session:{token} TTL'den silindiğinde değeri de onunla gider; SessionExpiryWatcher
            // o an "kimin oturumuydu" diye buradan okuyabilsin diye daha uzun ömürlü (mutlak
            // 2 saatlik sınırdan güvenli şekilde uzun) bir kopya tutuyoruz.
            var metaEntries = new HashEntry[]
            {
                new HashEntry("UserId", user.Id),
                new HashEntry("Username", user.Username)
            };
            await db.HashSetAsync($"session-meta:{sessionToken}", metaEntries);
            await db.KeyExpireAsync($"session-meta:{sessionToken}", TimeSpan.FromHours(3));

            await db.KeyDeleteAsync(failKey);

            AppMetrics.LoginAttemptsTotal.WithLabels("success").Inc();
            AppMetrics.ActiveSessions.Inc();

            string location = await _geoLocation.ResolveLocationAsync(clientIp);
            _logger.LogInformation("Kullanıcı giriş yaptı: {Username} (Id: {UserId}, IP: {ClientIp}, Konum: {Location})", user.Username, user.Id, clientIp, location);

            return Ok(new { token = sessionToken, username = user.Username });
        }
        catch (RedisException)
        {
            return StatusCode(503, "Servis şu anda kullanılamıyor. Lütfen daha sonra tekrar deneyin.");
        }
    }

    private static async Task RegisterFailedAttemptAsync(IDatabase db, string key)
    {
        long newCount = await db.StringIncrementAsync(key);
        if (newCount == 1)
        {
            await db.KeyExpireAsync(key, FailedAttemptLockout);
        }
    }

    // POST: api/users/logout
    [HttpPost("logout")]
    public async Task<ActionResult> Logout([FromHeader(Name = "Authorization")] string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Unauthorized("Token bulunamadı.");
        }

        try
        {
            var db = _redis.GetDatabase();
            var sessionKey = $"session:{token}";
            var sessionValues = await db.HashGetAsync(sessionKey, new RedisValue[] { "Username", "UserId" });
            var username = sessionValues[0];
            var sessionUserId = sessionValues[1];

            bool deleted = await db.KeyDeleteAsync(sessionKey);
            await db.KeyDeleteAsync($"session-meta:{token}");
            if (deleted)
            {
                AppMetrics.ActiveSessions.Dec();
                _logger.LogInformation(
                    "Kullanıcı çıkış yaptı: {Username} (Id: {UserId})",
                    username.IsNullOrEmpty ? "(bilinmiyor)" : username.ToString(),
                    sessionUserId.IsNullOrEmpty ? (int?)null : (int)sessionUserId);
            }

            return Ok(new { message = "Çıkış yapıldı." });
        }
        catch (RedisException)
        {
            return StatusCode(503, "Servis şu anda kullanılamıyor. Lütfen daha sonra tekrar deneyin.");
        }
    }
}