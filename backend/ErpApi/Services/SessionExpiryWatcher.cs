using StackExchange.Redis;

namespace ErpApi.Services;

/// <summary>
/// Bir kullanıcı hiçbir işlem yapmadan oturumu (session:{token} anahtarı) Redis'te
/// kendiliğinden zaman aşımına uğradığında (hareketsizlikten 5dk veya mutlak 2 saat
/// sınırından), bunu da explicit "/logout" çağrısı gibi audit'e yazar ve
/// AppMetrics.ActiveSessions sayacını düşürür.
///
/// Nasıl çalışır: Redis'in "keyspace notification" özelliğini kullanır - bir anahtarın
/// TTL'den dolayı silindiği anda Redis, "__keyevent@0__:expired" kanalına o anahtarın
/// adını yayınlar (pub/sub). Bu özellik varsayılan olarak kapalıdır, StartAsync içinde
/// CONFIG SET ile açılıyor.
///
/// Önemli detay: "expired" olayı geldiğinde anahtarın DEĞERİ artık Redis'te yoktur (silindiği
/// için tetiklenmiştir), sadece adı gelir. Bu yüzden UsersController.Login, session:{token}'ın
/// yanına daha uzun ömürlü bir "session-meta:{token}" kopyası da yazıyor; buradan
/// UserId/Username okunup sonra bu kopya da temizleniyor.
///
/// Not: Explicit "/logout" çağrısı KeyDeleteAsync ile silme yapar - Redis bunu "del" olayı
/// olarak yayınlar, "expired" olarak değil. Biz sadece "expired" kanalını dinlediğimiz için
/// explicit logout'ta bu servis tekrar tetiklenmez, çift log/çift sayaç düşürme olmaz.
/// </summary>
public class SessionExpiryWatcher : IHostedService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SessionExpiryWatcher> _logger;
    private ISubscriber? _subscriber;
    private static readonly RedisChannel ExpiredChannel = RedisChannel.Literal("__keyevent@0__:expired");

    public SessionExpiryWatcher(IConnectionMultiplexer redis, ILogger<SessionExpiryWatcher> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            // "Ex": sadece "bir anahtar TTL'den silindi" (expired) olaylarını yayınla.
            await server.ConfigSetAsync("notify-keyspace-events", "Ex");

            _subscriber = _redis.GetSubscriber();
            await _subscriber.SubscribeAsync(ExpiredChannel, OnKeyExpired);

            _logger.LogInformation("Oturum zaman aşımı dinleyicisi (SessionExpiryWatcher) başlatıldı.");
        }
        catch (Exception ex)
        {
            // Redis'e CONFIG SET izni yoksa (ör. bazı yönetilen Redis servisleri) uygulamanın
            // tamamı çökmesin; sadece bu özellik pasif kalır, uygulama normal çalışmaya devam eder.
            _logger.LogWarning(ex, "Oturum zaman aşımı dinleyicisi başlatılamadı, otomatik çıkış logları kaydedilmeyecek.");
        }
    }

    private void OnKeyExpired(RedisChannel channel, RedisValue expiredKey)
    {
        // Pub/sub callback'i senkron; asıl işi ayrı bir Task'ta yapıp callback'i hemen bırakıyoruz.
        _ = HandleExpiredKeyAsync(expiredKey.ToString());
    }

    private async Task HandleExpiredKeyAsync(string expiredKey)
    {
        const string prefix = "session:";
        if (!expiredKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return; // bizi ilgilendirmeyen bir anahtar (ör. failed:*, session-meta:* kendi TTL'iyle düşerse)
        }

        string token = expiredKey[prefix.Length..];
        var db = _redis.GetDatabase();
        var metaKey = $"session-meta:{token}";

        var values = await db.HashGetAsync(metaKey, new RedisValue[] { "Username", "UserId" });
        var username = values[0];
        var userId = values[1];

        await db.KeyDeleteAsync(metaKey);

        AppMetrics.ActiveSessions.Dec();
        _logger.LogInformation(
            "Kullanıcı oturumu zaman aşımına uğradı (otomatik çıkış): {Username} (Id: {UserId})",
            username.IsNullOrEmpty ? "(bilinmiyor)" : username.ToString(),
            userId.IsNullOrEmpty ? (int?)null : (int)userId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscriber != null)
        {
            await _subscriber.UnsubscribeAllAsync();
        }
    }
}
