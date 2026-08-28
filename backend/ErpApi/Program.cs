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
    // ASP.NET Core'un kendi "Request starting/finished", "Executing endpoint" gibi
    // dahili pipeline logları Information seviyesinde çok gürültülü olur ve asıl
    // audit olaylarını (kim ne yaptı) boğar; appsettings.json'daki niyetle aynı
    // şekilde bunları Warning'e çekiyoruz.
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    // EF Core her SQL sorgusunu Information seviyesinde tam metniyle loglar
    // ("Executed DbCommand ...") - bu da audit akışını boğar, aynı sebeple Warning'e çekiyoruz.
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
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

builder.Services.AddHealthChecks()
    .AddUrlGroup(new Uri("http://localhost:9090/-/healthy"), 
        name: "prometheus", 
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "monitoring" })
    .AddUrlGroup(new Uri("http://localhost:3000/api/health"), 
        name: "grafana", 
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "monitoring" })
    .AddUrlGroup(new Uri("http://localhost:3100/ready"), 
        name: "loki", 
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "monitoring" })
    .AddUrlGroup(new Uri("http://ip-api.com/json/"), 
        name: "ip-api", 
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "external" });

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!)
    .AddRedis("localhost:6379");

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