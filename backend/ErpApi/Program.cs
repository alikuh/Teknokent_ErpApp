using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using StackExchange.Redis;
using Serilog;
using Serilog.Sinks.Grafana.Loki;
using Prometheus;

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

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();

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

var app = builder.Build();

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

app.MapControllers();

app.Run();