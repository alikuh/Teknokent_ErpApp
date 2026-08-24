# İzleme (Monitoring) Sistemi — Prometheus + Loki + Grafana

Bu doküman, ErpApi'ye eklenen izleme/telemetri altyapısının **tamamını** anlatır: hangi
dosyalar eklendi, hangi kod satırları neden değişti, artıları/eksileri neler, ve sistemi
nasıl kullanıp genişleteceğiniz. Amaç: bu konuya sıfırdan giren birinin, bu dosyayı okuyunca
kurulan sistemin tamamına hakim olması.

---

## 1. Neden bu üç araç, ne işe yarıyor

İzlenmek istenen iki farklı veri türü var, ve her biri farklı bir araçla toplanıyor:

| Veri türü | Örnek | Araç | Neden |
|---|---|---|---|
| Sayısal, zaman serisi **metrikler** | "şu an kaç aktif oturum var", "dakikada kaç login denemesi oldu", "bir endpoint kaç ms sürüyor" | **Prometheus** | Bu tür veriyi verimli saklamak ve `rate()`, `sum()` gibi matematiksel sorgularla analiz etmek için özel olarak tasarlanmıştır. |
| Kişiye özel, metinsel **audit olayları** | "ahmet 14:32'de X ürününü sildi" | **Loki** | Prometheus'a kullanıcı adı gibi "yüksek çeşitlilikte" (cardinality) veri etiket olarak koymak performans sorunu yaratır (bkz. §7). Loki, log satırlarını arama/filtreleme için tasarlanmış, bu tür veri için doğru araçtır. |
| Görselleştirme | İkisini de tek ekranda grafik/tablo/log akışı olarak gösterme | **Grafana** | Hem Prometheus hem Loki'yi "datasource" olarak okuyup aynı dashboard'da birleştirebilen görselleştirme katmanı. |

**Genel akış:**

```
Backend (dotnet run, host üzerinde, :5077)
   │
   ├─ /metrics endpoint'i  ──(Prometheus 5 sn'de bir "çekiyor" - pull)──►  Prometheus (:9090)
   │                                                                            │
   └─ Serilog ──(log satırı oluşunca HTTP push, arka planda/asenkron)──►  Loki (:3100)
                                                                                │
                                                                                ▼
                                                                          Grafana (:3000)
                                                                    (ikisini de datasource
                                                                     olarak okuyup dashboard'da
                                                                     gösterir)
```

Backend, container içinde değil **host üzerinde** (`dotnet run`) çalışıyor; bu yüzden
Prometheus container'ının host'a ulaşabilmesi için `host.docker.internal` + `extra_hosts`
ayarı kullanıldı (bkz. §3).

---

## 2. Eklenen dosyaların tam listesi

```
monitoring/
├── prometheus/
│   └── prometheus.yml                  # Prometheus'a "nereyi, ne sıklıkla tara" der
├── loki/
│   └── loki-config.yml                 # Loki'nin depolama/çalışma ayarları
└── grafana/
    └── provisioning/
        ├── datasources/
        │   └── datasources.yml         # Grafana açılışta Prometheus+Loki'yi otomatik tanır
        └── dashboards/
            ├── dashboards.yml          # "bu klasördeki dashboard'ları otomatik yükle" tanımı
            └── erp-overview.json       # Hazır "ERP Genel Bakış" dashboard'u (5 panel)

backend/ErpApi/Services/AppMetrics.cs   # Yeni: uygulamaya özel Prometheus metrik tanımları
backend/ErpApi/Services/SessionExpiryWatcher.cs  # Yeni: TTL'den kendiliğinden biten oturumları da audit'e yazar

docs/MONITORING.md                       # Bu dosya
```

Değiştirilen (mevcut) dosyalar:

```
docker-compose.yml                                   # +3 servis: prometheus, loki, grafana
backend/ErpApi/ErpApi.csproj                          # +3 NuGet paketi
backend/ErpApi/Program.cs                             # Serilog + prometheus-net devreye alındı
backend/ErpApi/appsettings.json                       # Loki:Url ayarı eklendi
backend/ErpApi/Controllers/UsersController.cs         # audit log + metrik çağrıları
backend/ErpApi/Controllers/ProductsController.cs      # audit log çağrıları
backend/ErpApi/Controllers/SalesController.cs         # audit log çağrısı
```

---

## 3. `docker-compose.yml` — eklenen servisler

```yaml
prometheus:
  image: prom/prometheus:v2.55.1
  volumes:
    - ./monitoring/prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
    - prometheus_data:/prometheus
  ports: ["9090:9090"]
  extra_hosts:
    - "host.docker.internal:host-gateway"   # container'dan host'taki backend'e ulaşmak için

loki:
  image: grafana/loki:2.9.8
  command: -config.file=/etc/loki/loki-config.yml
  volumes:
    - ./monitoring/loki/loki-config.yml:/etc/loki/loki-config.yml:ro
    - loki_data:/loki
  ports: ["3100:3100"]

grafana:
  image: grafana/grafana:11.3.1
  environment:
    GF_SECURITY_ADMIN_USER: admin
    GF_SECURITY_ADMIN_PASSWORD: admin123      # ⚠️ bkz. §7 - değiştirilmeli
  volumes:
    - ./monitoring/grafana/provisioning:/etc/grafana/provisioning:ro
    - grafana_data:/var/lib/grafana
  ports: ["3000:3000"]
  depends_on: [prometheus, loki]
```

**Neden `extra_hosts: host-gateway`?** Backend `docker compose` dışında, host üzerinde
`dotnet run` ile çalışıyor (`http://0.0.0.0:5077`). Container'ların host'a erişebilmesi için
Docker'ın sağladığı `host.docker.internal` adresini kullanıyoruz; `host-gateway` bunu
Linux'ta da (Docker Desktop olmadan) çalışır hale getiriyor.

**Neden imaj versiyonları sabitlendi (`:latest` değil)?** `prom/prometheus:v2.55.1` gibi
belirli versiyonlar kullanıldı ki bir gün `docker compose pull` yaptığınızda beklenmedik bir
büyük sürüm güncellemesi (breaking change) sisteminizi bozmasın. `postgres:16` ve `redis:7`
zaten aynı mantıkla (major versiyon) sabitlenmişti, aynı yaklaşımı sürdürdük.

---

## 4. `monitoring/prometheus/prometheus.yml`

```yaml
global:
  scrape_interval: 5s

scrape_configs:
  - job_name: "erp-api"
    metrics_path: /metrics
    static_configs:
      - targets: ["host.docker.internal:5077"]
```

Prometheus'a "5 saniyede bir `host.docker.internal:5077/metrics`'e GET at, oradaki metin
formatındaki metrikleri oku ve zaman serisi olarak sakla" diyor. Bu **pull (çekme)** modeli
— backend'in Prometheus'a bir şey göndermesi gerekmiyor, sadece pasif olarak
`/metrics`'i açık tutuyor.

`scrape_interval: 5s` bilinçli olarak agresif seçildi (dashboard'un `refresh: 5s`'i ile
uyumlu olsun, demo/geliştirmede anlık görünsün diye). Gerçek üretimde `15s` (Prometheus'un
kendi varsayılanı) diskte daha az yer kaplar, performans farkı ihmal edilebilir düzeydedir.

---

## 5. `monitoring/loki/loki-config.yml`

Loki'nin resmi "tek node, dosya sistemine yaz" örnek konfigürasyonunun küçültülmüş hali.
Önemli noktalar:
- `auth_enabled: false` — tek kullanıcılı geliştirme ortamı, multi-tenant auth gerekmiyor.
- `storage.filesystem` — verileri container içindeki `/loki` klasörüne yazar, bu da
  `loki_data` named volume'una bağlanmıştır (container silinse bile veri kalır).

---

## 6. `monitoring/grafana/provisioning/` — otomasyon dosyaları

**`datasources/datasources.yml`:** Grafana her açıldığında Prometheus (`http://prometheus:9090`)
ve Loki (`http://loki:3100`)'yi elle "Add data source" tıklamadan otomatik tanır. `uid: prometheus`
ve `uid: loki` sabit ID'ler verildi ki dashboard JSON'u bu ID'lere referans verebilsin.

**`dashboards/dashboards.yml`:** "bu klasördeki (`/etc/grafana/provisioning/dashboards`) tüm
JSON dosyalarını dashboard olarak yükle, 30 saniyede bir değişiklik var mı kontrol et" der.
`erp-overview.json`'u her değiştirdiğimizde sadece `docker compose restart grafana` (veya
30 saniye beklemek) yeterli, elle içe aktarma gerekmiyor.

**`dashboards/erp-overview.json`:** "ERP Genel Bakış" dashboard'u, 5 panel:

| # | Panel | Tür | Ne gösteriyor | Sorgu (özet) |
|---|---|---|---|---|
| 1 | Aktif Oturum Sayısı | stat | Şu an Redis'te aktif olan oturum sayısı | `erp_active_sessions` |
| 2 | Login Denemeleri | timeseries | Başarılı/başarısız login oranı | `sum by (result) (rate(erp_login_attempts_total[1m]))` |
| 3 | Endpoint Bazlı İstek Oranı | timeseries | Hangi endpoint ne sıklıkla çağrılıyor, hangi HTTP kodlarıyla | `http_requests_received_total` üzerinden, Dashboard controller'ının 3 alt-endpoint'i tek çizgide birleştirilmiş (bkz. §6.1) |
| 4 | Audit Olay Akışı | logs | "Kim ne yaptı" — Loki'den gelen okunaklı log satırları | `{app="erp-api"} \| json \| line_format "[{{.level}}] {{.Message}}"` |
| 5 | Kullanıcı Bazında İşlem Sayısı | bargauge | Son 1 saatte hangi kullanıcı kaç işlem yapmış (anormal aktiviteyi gözle yakalamak için) | `sum by (UserId) (count_over_time({app="erp-api"} \| json \| UserId != "" [1h]))` |

### 6.1 Neden Dashboard endpoint'leri tek çizgide birleştirildi

`DashboardController`'ın `GetSummary`, `GetSalesByDay`, `GetTopProducts` metodları, kullanıcı
uygulamanın dashboard sayfasına her girdiğinde **birebir aynı sayıda** çağrılıyor (üçü de
sayfa yüklenince beraber ateşleniyor). Bu yüzden onları ayrı ayrı çizgi olarak göstermek
yerine, PromQL'in `label_replace()` fonksiyonuyla üçünün `action` etiketini tek bir isme
(`"Dashboard'a bakma"`) zorlayıp `sum by` ile birleştirdik. Diğer controller'lar (Products,
Sales, Users) eskisi gibi ayrı ayrı görünmeye devam ediyor.

---

## 7. Backend kod değişiklikleri (dosya dosya)

### 7.1 `ErpApi.csproj` — yeni paketler

```xml
<PackageReference Include="prometheus-net.AspNetCore" Version="8.2.1" />
<PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
<PackageReference Include="Serilog.Sinks.Grafana.Loki" Version="9.0.2" />
```

- **prometheus-net.AspNetCore**: `/metrics` endpoint'ini açar, her HTTP isteğini otomatik
  sayar/süresini ölçer (`UseHttpMetrics()`), ve elle özel metrik (counter/gauge) tanımlamamıza
  izin verir (`Metrics.CreateCounter(...)`).
- **Serilog.AspNetCore**: `Microsoft.Extensions.Logging`'in yerine geçen, "structured
  logging" (yapılandırılmış — sabit metin yerine anahtar-değer çiftleri) destekleyen log
  kütüphanesi. `_logger.LogInformation("... {Username}", username)` yazdığınızda hem okunaklı
  metin hem de ayrı ayrı sorgulanabilir alanlar (`Username`) üretir.
- **Serilog.Sinks.Grafana.Loki**: Serilog'un ürettiği log olaylarını Loki'ye HTTP ile (arka
  planda, batch halinde, asenkron) gönderen eklenti ("sink").

### 7.2 `Program.cs` — Serilog ve metrik middleware'i devreye alma

```csharp
string lokiUrl = builder.Configuration["Loki:Url"] ?? "http://localhost:3100";
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", "erp-api")
    .WriteTo.Console()
    .WriteTo.GrafanaLoki(lokiUrl, labels: new[] { new LokiLabel { Key = "app", Value = "erp-api" } }));
```

- `MinimumLevel.Override(...)`: ASP.NET Core'un ("Request starting/finished") ve EF Core'un
  ("Executed DbCommand ...") kendi dahili Information seviyesindeki logları **Warning**'e
  çekildi. Bu iki kategori susturulmasaydı, her HTTP isteği ve her SQL sorgusu Loki'ye ayrı
  satır olarak gidip asıl audit olaylarını (kim ne yaptı) gürültüye boğardı — geliştirme
  sırasında canlı olarak bu sorunu yaşayıp düzelttik (bkz. §9 "Karşılaşılan sorunlar").
- `Enrich.WithProperty("app", "erp-api")`: Her log satırına sabit bir etiket ekler; Grafana'daki
  log paneli `{app="erp-api"}` ile bunu arıyor.
- `WriteTo.Console()` korunuyor — terminalden log izlemeye devam edebilirsiniz, Loki bunun
  yerine değil, **yanına** eklendi.

```csharp
app.UseHttpMetrics();  // her isteği method/route/status koduna göre sayar ve süresini ölçer
app.MapMetrics();      // /metrics endpoint'ini açar
```

Route **template** (`api/Products/{id}`) kullanılıyor, gerçek URL (`api/Products/17`)
değil — bu yüzden binlerce farklı ürün ID'si Prometheus'ta binlerce ayrı seri açmıyor
(cardinality güvenli, bkz. §9).

### 7.3 `Services/AppMetrics.cs` — özel metrik tanımları (yeni dosya)

```csharp
public static class AppMetrics
{
    public static readonly Counter LoginAttemptsTotal = Metrics.CreateCounter(
        "erp_login_attempts_total", "...", new CounterConfiguration { LabelNames = new[] { "result" } });

    public static readonly Gauge ActiveSessions = Metrics.CreateGauge(
        "erp_active_sessions", "...");

    public static readonly Counter FailedLoginLockoutsTotal = Metrics.CreateCounter(
        "erp_failed_login_lockouts_total", "...", new CounterConfiguration { LabelNames = new[] { "scope" } });
}
```

`prometheus-net`'in otomatik topladığı HTTP metrikleri (`http_requests_received_total` vb.)
sadece "kaç istek geldi" bilgisini verir; **iş anlamı taşıyan** ("login başarılı mı oldu",
"kaç kişi şu an oturum açık") metrikler bunlar gibi elle tanımlanır.

**Önemli tasarım kararı — etiketler (label) kasıtlı olarak kısıtlı tutuldu:** `result` sadece
`"success"/"failure"`, `scope` sadece `"user"/"ip"` değeri alabiliyor. Kullanıcı adı veya IP
adresi gibi **sınırsız çeşitlilikte** (yüksek cardinality) veri asla Prometheus etiketi
yapılmadı — her farklı değer ayrı bir zaman serisi demektir ve binlerce kullanıcıyla
Prometheus'un belleğini/diskini şişirir. Kişiye özel bilgi bunun yerine **Loki tarafında**
(log metninde) tutuluyor.

### 7.4 `Controllers/UsersController.cs`

- Constructor'a `ILogger<UsersController>` enjekte edildi.
- `Register`: başarılı kayıtta `_logger.LogInformation(...)`.
- `Login`: her dallanmada (IP kilidi, kullanıcı kilidi, kullanıcı yok, şifre yanlış, başarılı)
  hem ilgili `AppMetrics` sayacı artırılıyor hem de `_logger.LogWarning`/`LogInformation` ile
  audit satırı yazılıyor. Başarılı girişte ayrıca `ActiveSessions.Inc()`.
- `Logout`: session'dan silinmeden önce `Username` **ve** `UserId` okunuyor (başlangıçta
  sadece `Username` vardı, kullanıcı bazlı analiz için tutarlılık amacıyla sonradan `UserId`
  de eklendi — bkz. §9), `ActiveSessions.Dec()` ve audit log.

### 7.4.1 `Services/SessionExpiryWatcher.cs` — TTL'den kendiliğinden biten oturumlar (yeni)

**Sorun:** Bir kullanıcı hiçbir işlem yapmadan `session:{token}` Redis'te TTL'den (5 dk
hareketsizlik veya 2 saat mutlak sınır) kendiliğinden silinirse, hiçbir kod tetiklenmiyordu —
ne "çıkış yaptı" audit logu yazılıyordu, ne de `erp_active_sessions` sayacı düşürülüyordu
(bu ikincisi zamanla sayacın gerçek değerden sapmasına yol açan ayrı bir hataydı).

**Çözüm:** Redis'in **keyspace notification** özelliği kullanıldı — bir anahtar TTL'den
silindiğinde Redis, `__keyevent@0__:expired` pub/sub kanalına o anahtarın adını yayınlar.
`SessionExpiryWatcher` adında yeni bir `IHostedService` bu kanalı dinliyor:

1. Uygulama başlarken `CONFIG SET notify-keyspace-events Ex` ile bu özelliği açıyor (varsayılan
   kapalı gelir). Bu komut "admin" yetkisi istediği için Redis bağlantı dizesine
   `allowAdmin=true` eklendi (`Program.cs`).
2. `session:*` deseniyle eşleşen bir "expired" olayı geldiğinde, token'ı çıkarıp
   `session-meta:{token}` anahtarından `UserId`/`Username`'i okuyor.
3. **Neden ayrı bir `session-meta:{token}` anahtarı var?** "expired" olayı geldiğinde asıl
   anahtarın (`session:{token}`) değeri Redis'te artık yok — sadece silindiği bilgisi geliyor.
   Bu yüzden `Login` sırasında, aynı bilgiyi (`UserId`, `Username`) daha uzun ömürlü (3 saat,
   mutlak 2 saatlik oturum sınırından güvenli şekilde uzun) bir kopya olarak da yazıyoruz;
   `SessionExpiryWatcher` bu kopyadan okuyup sonra onu da temizliyor. Açık `Logout` çağrısı da
   kendi `session-meta` kopyasını temizliyor ki 3 saat boyunca ortada gereksiz kalmasın.
4. Bulduğu bilgiyle `_logger.LogInformation("Kullanıcı oturumu zaman aşımına uğradı (otomatik
   çıkış): ...")` yazıyor ve `AppMetrics.ActiveSessions.Dec()` çağırıyor.

**Neden çift log/çift sayaç düşürme riski yok:** Açık `Logout` çağrısı `KeyDeleteAsync` ile
siliyor — Redis bunu `del` olayı olarak yayınlar, `expired` olarak değil.
`SessionExpiryWatcher` sadece `expired` kanalını dinlediği için, açık logout'ta tekrar
tetiklenmiyor.

**Dayanıklılık:** `CONFIG SET` bir sebepten başarısız olursa (örn. bazı yönetilen Redis
servisleri admin komutlarını kapatır), `try/catch` ile sadece bir `LogWarning` yazılıp bu
özellik pasif kalıyor — uygulamanın geri kalanı etkilenmiyor. Bu, geliştirme sırasında canlı
olarak test edilip doğrulandı (bkz. §9).

### 7.5 `Controllers/ProductsController.cs`, `SalesController.cs`

Aynı desen: constructor'a `ILogger` eklendi, `Create`/`Update`/`Delete` (ve `CreateSale`)
işlemlerinin sonuna tek satırlık `_logger.LogInformation("Kullanıcı {UserId} ... {ProductId} ...")`
audit logu eklendi. Okuma (`GET`) endpoint'lerine log eklenmedi — bilinçli tercih: "kim
neyi *değiştirdi*" audit için önemlidir, her listeleme isteğini loglamak sadece gürültü
üretir.

### 7.6 `appsettings.json`

```json
"Loki": { "Url": "http://localhost:3100" }
```

Loki adresi appsettings üzerinden yapılandırılabilir yapıldı (kod içine sabit yazılmadı) —
ortam değişince (örn. production'da farklı bir Loki adresi) sadece config değişir, kod
değişmez.

---

## 8. Nasıl çalıştırılır / kullanılır

```bash
# İzleme yığınını başlat (bir kere, arka planda çalışmaya devam eder)
docker compose up -d prometheus loki grafana

# Backend'i her zamanki gibi çalıştır
cd backend/ErpApi
dotnet run
```

- **Grafana:** http://localhost:3000 — `admin` / `admin123` *(bkz. §10, değiştirilmeli)*
  → Dashboards → **ERP Genel Bakış**
- **Prometheus (ham sorgu/hedef kontrolü için):** http://localhost:9090 →
  Status → Targets (backend `UP` mi diye bakmak için)
- **Backend'in kendi `/metrics` çıktısı:** http://localhost:5077/metrics

Dashboard JSON'unu değiştirdikten sonra görmek için: `docker compose restart grafana`.

---

## 9. Kurulum sırasında karşılaşılan ve çözülen sorunlar

Bunlar, sistemi "olması gerektiği gibi" hale getirene kadar canlı olarak yaşayıp
düzelttiğimiz gerçek sorunlar — ileride benzer bir şey kurarken işinize yarar:

1. **`UseHttpMetrics`/`MapMetrics` bulunamadı derleme hatası** → `using Prometheus;`
   eksikti, extension metotları o namespace'te.
2. **`erp_` metrikleri `/metrics`'te görünmüyordu** → .NET'te `static readonly` alanlar
   "ilk kullanımda" (lazy) başlatılır; login/register endpoint'ine hiç istek gitmeden
   `AppMetrics` sınıfının statik constructor'ı hiç çalışmamıştı. İlk isteklerden sonra
   normal şekilde ortaya çıktı.
3. **ASP.NET Core + EF Core'un dahili Information logları Loki'yi doldurdu** →
   appsettings.json'daki `Logging:LogLevel` şeması Serilog'un okuduğu `Serilog` şemasıyla
   birebir örtüşmüyor; `MinimumLevel.Override(...)` ile kod içinde açıkça bastırıldı (§7.2).
4. **Log panelinde `{{.Level}}` boş geliyordu** → Serilog'un Loki sink'i `level` bilgisini
   JSON gövdesine değil, ayrı bir Loki **etiketi** (stream label) olarak gönderiyor; doğru
   kullanım küçük harfle `{{.level}}`.
5. **Grafana dashboard'unda datasource bulunamadı hatası** → `datasources.yml`'da `uid` alanı
   belirtilmemişti, Grafana rastgele bir uid üretiyordu; dashboard JSON'u ise sabit
   `"uid": "prometheus"` / `"uid": "loki"` bekliyordu. `datasources.yml`'a açıkça `uid:`
   eklenerek eşleştirildi.
6. **Test sırasında port 5077 çakışması / yanlışlıkla kullanıcının kendi `dotnet run`
   sürecinin durdurulması** → arka planda başlattığımız test süreçlerini `fuser -k` ile
   kapatırken, kullanıcının kendi başlattığı gerçek geliştirme sürecini de durdurmuş
   olabileceğimiz fark edildi. Ders: paylaşılan bir portu kapatmadan önce o sürecin kime ait
   olabileceğini düşünmek gerekiyor. (Bu dersten sonra, sonraki testler kullanıcının portuna
   hiç dokunmadan ayrı bir test portunda -5078- yapıldı.)
7. **`SessionExpiryWatcher` başlarken `RedisCommandException: admin mode is enabled: CONFIG`
   hatası verdi** → `CONFIG SET` gibi yönetimsel komutlar StackExchange.Redis'te varsayılan
   olarak kapalıdır (yanlışlıkla tehlikeli bir komut çalıştırılmasın diye).
   `ConnectionMultiplexer.Connect("localhost:6379,allowAdmin=true")` ile açıldı. Bu hata
   sırasında uygulamanın çökmemiş olması (sadece `LogWarning` yazıp devam etmesi),
   `SessionExpiryWatcher.StartAsync` içindeki `try/catch`'in tam da amaçlandığı gibi
   çalıştığının kanıtıydı.

---

## 10. Artıları

- **Gerçek zamanlıya yakın görünürlük**: sistemde kim ne zaman giriş yaptı, hangi ürün
  silindi, hangi endpoint yavaşladı — hepsi tek ekranda, 5 saniyede bir güncellenerek.
  Bu konuşma sırasında bu görünürlük sayesinde **gerçek bir performans sorununu** (login'in
  bazen 1-2 saniyeye çıkması, Argon2 + bellek darboğazı) somut verilerle teşhis
  edebildik — izleme sistemi olmadan bu sadece "bazen yavaş" hissi olarak kalırdı.
- **Düşük performans maliyeti**: Prometheus "pull" modeliyle çalışıyor (backend'e ekstra
  yük bindirmiyor), sayaçlar bellekte basit artırma işlemi, Loki'ye log gönderimi asenkron/
  batch halinde — request'leri bloklamıyor (detaylı ölçüm/açıklama için önceki konuşma
  kayıtlarına bakılabilir).
- **Cardinality-güvenli tasarım**: kullanıcı adı/IP gibi veriler hiçbir yerde Prometheus
  etiketi yapılmadı, sadece Loki log metninde tutuluyor — Prometheus'un en yaygın "acemi
  hatası"ndan baştan kaçınıldı.
- **Kod tabanına minimal, dağınık olmayan müdahale**: her controller'a sadece birkaç satır
  log çağrısı eklendi, iş mantığı hiçbir yerde değişmedi.
- **Otomatik provisioning**: Grafana'yı her açtığınızda datasource'ları elle eklemenize,
  dashboard'u elle import etmenize gerek yok — hepsi dosyadan otomatik yükleniyor, dolayısıyla
  git'e commit edilip başka bir makinede `docker compose up` ile bire bir aynı ortam
  kurulabiliyor.

## 11. Eksileri / bilinen sınırlamalar

Bunlar "yanlış yapıldı" değil, bilinçli olarak **geliştirme ortamına uygun, production için
henüz eksik** bırakılan noktalar:

- **Grafana admin şifresi** (`admin123`) docker-compose.yml içinde açık metin — sadece
  localhost'a bağlı bir geliştirme ortamı için kabul edilebilir, ama production'a taşınırsa
  mutlaka değiştirilmeli/secret olarak yönetilmeli.
- **Saklama süresi (retention) ayarlanmadı**: Prometheus ve Loki şu an sınırsız disk
  kullanıyor (varsayılan davranış), zamanla disk dolabilir. Production'a geçerken
  `--storage.tsdb.retention.time` (Prometheus) ve Loki tarafında retention config'i
  eklenmeli.
- **Alerting kurulmadı**: `erp_failed_login_lockouts_total` gibi metrikler üzerinden "5
  dakikada X'ten fazla başarısız giriş varsa uyar" gibi otomatik bildirim henüz yok — bunun
  için bir bildirim kanalı (e-posta/Slack/Telegram) seçilip Grafana Alerting'de kurulması
  gerekiyor.
- **Tek node, yüksek erişilebilirlik (HA) yok**: Loki ve Prometheus tek container olarak
  çalışıyor; container düşerse o an için veri toplama durur (geçmiş veri volume'da kalıcı
  olarak korunur). Küçük/orta ölçekli bir ERP için bu tamamen yeterli, çok büyük ölçekte
  cluster kurulumu gerekir.
- **Kimlik doğrulama yok**: Prometheus (`:9090`) ve Loki (`:3100`) şu an şifresiz erişilebilir
  — sadece localhost'a bağlı olduğu için sorun değil, ama sunucu dışarıya açık bir ortama
  taşınırsa bu portların dışarıdan erişilememesi (firewall/reverse proxy arkasında olması)
  gerekir.
- **Okuma (GET) endpoint'leri audit loglanmıyor**: bilinçli bir tercih (§7.5) ama isterseniz
  "kim neyi görüntüledi" seviyesinde bir ihtiyaç doğarsa bu genişletilebilir.
- **Kaynak kullanımı düşük-orta düzeyde bir yük ekliyor**: özellikle kısıtlı RAM'li
  geliştirme ortamlarında (bkz. bu konuşmadaki login-yavaşlığı teşhisi) üç konteynerin
  toplam ~300-350MB'lık bellek ayak izi, zaten dar olan bir bütçede hissedilebilir hale
  gelebilir.

## 12. Sonraki adımlar için fikirler

İsterseniz ileride şunlar eklenebilir (hiçbiri şu an kurulu değil, sadece öneri):

- Grafana Alerting: başarısız login patlaması, `/metrics` hedefinin `DOWN` olması gibi
  durumlar için bildirim.
- Prometheus/Loki retention ayarları (disk büyümesini sınırlamak için).
- `docs/MONITORING.md`'deki gibi, frontend tarafına da (varsa) benzer bir izlenebilirlik
  eklenmesi.
- Argon2 parametrelerinin bu ortamdaki kaynak kısıtına göre yeniden ayarlanması (ayrı bir
  konu, güvenlik/performans dengesi gerektirir — kullanıcıyla konuşulmadan değiştirilmedi).
