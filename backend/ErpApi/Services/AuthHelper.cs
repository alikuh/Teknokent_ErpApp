using StackExchange.Redis;

namespace ErpApi.Services;

public static class AuthHelper
{
    private static readonly TimeSpan SlidingSessionTimeout = TimeSpan.FromMinutes(5);

    public static async Task<int?> GetUserIdAsync(IConnectionMultiplexer redis, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        string sessionKey = $"session:{token}";

        try
        {
            var db = redis.GetDatabase();
            var values = await db.HashGetAsync(sessionKey, new RedisValue[] { "UserId", "AbsoluteExpiry" });
            var userIdValue = values[0];
            var absoluteExpiryValue = values[1];

            if (userIdValue.IsNullOrEmpty)
            {
                return null;
            }

            if (!absoluteExpiryValue.IsNullOrEmpty)
            {
                long absoluteExpiry = (long)absoluteExpiryValue;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= absoluteExpiry)
                {
                    await db.KeyDeleteAsync(sessionKey);
                    return null;
                }
            }

            // Istek gecerli oldugu icin hareketsizlik zaman asimini yenile.
            await db.KeyExpireAsync(sessionKey, SlidingSessionTimeout);

            return (int)userIdValue;
        }
        catch (RedisException)
        {
            return null;
        }
    }
}
