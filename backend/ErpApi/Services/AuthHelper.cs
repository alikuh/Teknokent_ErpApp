using StackExchange.Redis;

namespace ErpApi.Services;

public static class AuthHelper
{
    public static async Task<int?> GetUserIdAsync(IConnectionMultiplexer redis, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var db = redis.GetDatabase();
        var userIdValue = await db.HashGetAsync($"session:{token}", "UserId");

        if (userIdValue.IsNullOrEmpty)
        {
            return null;
        }

        return (int)userIdValue;
    }
}