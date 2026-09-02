using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using ErpApi.Data;
using ErpApi.Models;
using StackExchange.Redis;
using Serilog;
using Serilog.Sinks.Grafana.Loki;
using Prometheus;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Serilog: hem konsola (eskisi gibi) hem de Loki'ye (Grafana'da görmek için)
// yapılandırılmış (structured) log yazar. Her log satırına "app=erp-api"
// etiketi eklenir; Grafana dashboard'undaki log paneli bu etiketi arıyor.
string lokiUrl = builder.Configuration["Loki:Url"] ?? "http://localhost:3100";
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .MinimumLevel.Information()
    // Loki'ye SADECE anlamlı olaylar (audit + gerçek hata/uyarı) gitsin diye
    // framework gürültüsünü Warning'e çekiyoruz. Bunlar Information seviyesinde
    // saniyede onlarca satır üretip asıl "kim ne yaptı" loglarını boğuyor:
    //  - Microsoft.*       : ASP.NET pipeline ("Request starting/finished",
    //                        "Executing endpoint"), routing, EF Core'un her SQL'i
    //                        ("Executed DbCommand"), health check "completed" logları
    //  - System.Net.Http.* : HttpClient'in her isteği "Sending/Received HTTP request"
    //                        diye loglaması (GeoLocationService + health check'ler)
    //  - Npgsql.*          : ham PostgreSQL sürücüsünün bağlantı/komut logları
    //                        (health check'in AddNpgSql'i her 30 sn'de bağlanıyor)
    // Uygulamanın kendi logları (ErpApi.*) Information'da kalır, etkilenmez.
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Npgsql", Serilog.Events.LogEventLevel.Warning)
    // Başlangıçtaki "Now listening on..." / "Application started" satırları faydalı,
    // onları Information'da tutuyoruz.
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", "erp-api")
    .WriteTo.Console()
    .WriteTo.GrafanaLoki(
        lokiUrl,
        labels: new[] { new LokiLabel { Key = "app", Value = "erp-api" } }));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<IConnectionMultiplexer>(
    // allowAdmin=true: SessionExpiryWatcher'ın "notify-keyspace-events" ayarını
    // CONFIG SET ile açabilmesi için gerekli (StackExchange.Redis bu tür yönetimsel
    // komutları varsayılan olarak, yanlışlıkla çalıştırılmasın diye kapalı tutar).
    ConnectionMultiplexer.Connect("localhost:6379,allowAdmin=true"));

// Oturum TTL'den (hareketsizlik/mutlak süre) kendiliğinden silindiğinde de audit log
// yazsın ve erp_active_sessions sayacını düşürsün diye - bkz. Services/SessionExpiryWatcher.cs
builder.Services.AddHostedService<ErpApi.Services.SessionExpiryWatcher>();

// Başarılı girişlerde IP'den kaba şehir/ülke tahmini için (bkz. Services/GeoLocationService.cs)
builder.Services.AddHttpClient<ErpApi.Services.IGeoLocationService, ErpApi.Services.GeoLocationService>();

// Cari hesap hesaplamaları (bakiye, FIFO kalan, yaşlandırma, ekstre) ve
// bunların ihtiyaç duyduğu fiş+tahsilat yüklemesi - bkz. Services/Ledger*.cs
builder.Services.AddScoped<ErpApi.Services.LedgerRepository>();
builder.Services.AddSingleton<ErpApi.Services.LedgerService>();

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Enum'lar (ör. ReceiptType) JSON'da sayı değil, isimleriyle
        // ("Veresiye") gidip gelsin - frontend okunur değerler gönderiyor.
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// Health check.
//  - postgres / redis: API'nin çalışması için ŞART. Biri düşerse /health -> 503.
//  - ngrok: KRITIK DEĞİL (yerel kullanıcılar tünel olmadan da çalışır), o yüzden
//    failureStatus=Degraded -> düşse bile /health 200 kalır, sadece panelde
//    "DOWN" görünür. Yerel agent API'sine (localhost:4040) bakar, hızlıdır.
// Monitoring yığını (Prometheus/Grafana/Loki) ve ip-api bilerek DIŞARIDA:
// onları Prometheus doğrudan tarıyor (up{job=...}), buraya yavaş/dış HTTP
// çağrısı koymak /healthmetrics scrape'inde istekleri biriktirip sunucuyu kilitler.
// timeout: bir bağımlılık asılı kalırsa birkaç sn sonra "unhealthy" de, sonsuz bekleme.
builder.Services.AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")!,
        name: "postgres",
        timeout: TimeSpan.FromSeconds(3))
    .AddRedis(
        // Yeni bağlantı açma; Program.cs'te zaten kayıtlı olan singleton
        // ConnectionMultiplexer'ı tekrar kullan.
        sp => sp.GetRequiredService<IConnectionMultiplexer>(),
        name: "redis",
        timeout: TimeSpan.FromSeconds(3))
    .AddUrlGroup(
        // API host'ta çalıştığı için localhost; ileride konteynerleşirse
        // host.docker.internal:4040 olur. --inspect=false ile başlatılırsa
        // 4040 açılmaz, o zaman kendi --web-addr adresine göre güncelle.
        new Uri("http://localhost:4040/api/tunnels"),
        name: "ngrok",
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "tunnel" },
        timeout: TimeSpan.FromSeconds(2));

// CSRF çerezinin (csrf_token) tarayıcılar arası (frontend farklı porttan
// servis ediliyor) gidip gelebilmesi için origin'in "*" değil, açıkça
// belirtilmiş olması ve AllowCredentials() gerekiyor - tarayıcılar
// credentialed isteklerde wildcard origin'e izin vermiyor.
string[] allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5500" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// ngrok, istekleri kendi edge sunucularından yerel makinemize tünelleyerek
// iletiyor - bu yüzden Kestrel'in gördüğü bağlantı IP'si her zaman
// loopback (127.0.0.1) olur, gerçek client IP'si sadece X-Forwarded-For
// header'ında gelir (GeoLocationService ve login rate-limit hep "Yerel ağ"a
// düşmesinin sebebi buydu). ngrok'un edge IP'leri sabit/bilinen bir liste
// olmadığından KnownProxies/KnownNetworks kısıtlamasını kaldırıyoruz - yani
// bu header'a koşulsuz güveniyoruz. Bu üretimde spoofing riski taşır; sadece
// ngrok arkasında geliştirme/demo amaçlı kabul edilebilir.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Pipeline'daki her şeyden önce çalışmalı ki RemoteIpAddress, kendisinden
// sonraki middleware'lere (loglama, rate-limit, controller'lar) gerçek
// client IP'siyle ulaşsın.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "ErpApi v1");
});

app.UseHttpsRedirection();

// frontend/'i doğrudan bu backend'den servis ediyoruz - böylece tek port
// (ve dışarı açarken tek ngrok tüneli) yeterli oluyor, ayrıca aynı origin
// olduğu için CORS'a da gerek kalmıyor. Live Server (localhost:5500) ile
// yerelde geliştirmeye devam edebilirsiniz, o zaman CORS politikası devreye girer.
var frontendPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "frontend"));
var frontendFiles = new PhysicalFileProvider(frontendPath);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFiles });

app.UseCors("AllowFrontend");

// prometheus-net: her isteği method/route/status koduna göre otomatik sayar
// ve süresini ölçer (raw URL değil route template kullanır, bu yüzden
// cardinality güvenlidir), sonra /metrics endpoint'inden dışarı verir.
app.UseHttpMetrics();
app.MapMetrics();

// CORS'tan sonra, route eşleşmesinden önce - bkz. Middleware/CsrfMiddleware.cs
app.UseMiddleware<ErpApi.Middleware.CsrfMiddleware>();

// Frontend sayfaları (login/register) ilk açıldığında, kullanıcı henüz hiçbir
// API çağrısı yapmamışken bile csrf_token çerezinin oluşmuş olması için
// çağırdığı, güvenli (sadece GET) bir "ısındırma" endpoint'i.
app.MapGet("/api/csrf-token", () => Results.Ok());

app.UseHealthChecksPrometheusExporter("/healthmetrics", options =>
{
    options.ResultStatusCodes[HealthStatus.Healthy] = StatusCodes.Status200OK;
});

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.ToString()
            })
        });
        await context.Response.WriteAsync(result);
    }
});
app.MapControllers();

app.Run();