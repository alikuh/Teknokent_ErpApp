# Teknokent ErpApp — Proje Tanıtım Yazısı

Bu yazı, projenin ilk commit'inden bugüne kadar neyin, neden yapıldığını ve hangi
teknolojilerin kullanıldığını baştan sona anlatır. Amaç, projeye sonradan dahil olacak
birinin (ya da birkaç ay sonra kendinize dönüp bakacak sizin) tek bir dosyadan tüm resmi
görebilmesi.

---

## 1. Proje nedir

**ErpApp**, küçük ölçekli bir işletmenin ürün/stok ve satış süreçlerini yönetmesi için
yazılmış, çok kullanıcılı (multi-tenant) basit bir **ERP (kurumsal kaynak planlama)**
uygulamasıdır. Her kullanıcı kendi ürünlerini, stoklarını ve satışlarını yönetir; veriler
kullanıcı bazında izole edilmiştir.
Uygulama iki ana parçadan oluşur:

- **Backend:** ASP.NET Core (.NET 10) ile yazılmış bir REST API (`backend/ErpApi`)
- **Frontend:** Herhangi bir framework kullanmayan, saf HTML/CSS/JavaScript ile yazılmış
  statik sayfalar (`frontend/`)

Bunların yanına, geliştirme sürecinde bir de **izleme (monitoring)** yığını eklendi
(Prometheus + Loki + Grafana), çünkü canlı bir sistemde "neler oluyor" sorusuna cevap
vermek başlı başına bir ihtiyaç haline geldi.

---

## 2. Kullanılan teknolojiler

| Katman | Teknoloji | Ne için kullanılıyor |
|---|---|---|
| Backend framework | **ASP.NET Core / .NET 10** (Web API) | REST API, controller tabanlı uçlar |
| Veritabanı | **PostgreSQL 16** | Kullanıcı, ürün, satış verilerinin kalıcı depolanması |
| ORM | **Entity Framework Core** (`Npgsql.EntityFrameworkCore.PostgreSQL`) | Veritabanı erişimi, migration yönetimi |
| Önbellek / Oturum deposu | **Redis 7** (`StackExchange.Redis`) | Oturum (session) yönetimi, başarısız giriş sayaçları (rate limiting), keyspace notification |
| Parola hashleme | **Argon2id** (`Konscious.Security.Cryptography.Argon2`) | Parolaların geri döndürülemez şekilde saklanması |
| API dokümantasyonu | **Swagger / Swashbuckle** + `Microsoft.AspNetCore.OpenApi` | `/swagger` üzerinden interaktif API dokümanı |
| Loglama | **Serilog** (`Serilog.AspNetCore`, `Serilog.Sinks.Grafana.Loki`) | Yapılandırılmış (structured) log, hem konsola hem Loki'ye |
| Metrik | **prometheus-net.AspNetCore** | HTTP metrikleri + özel iş metrikleri (`/metrics` endpoint'i) |
| İzleme/görselleştirme | **Prometheus**, **Grafana Loki**, **Grafana** | Metrik toplama, log toplama, tek ekranda görselleştirme |
| Konum tespiti | **ip-api.com** (dış HTTP servis) | Giriş yapan kullanıcının IP'sinden yaklaşık şehir/ülke bilgisi |
| Frontend | Saf **HTML5 / CSS3 / Vanilla JavaScript** | Framework'süz, bağımlılıksız arayüz |
| Konteynerleştirme | **Docker / Docker Compose** | Postgres, Redis, RedisInsight, Prometheus, Loki, Grafana servislerinin ayağa kaldırılması |
| Kaynak kontrol | **Git** | Versiyon kontrolü |

Backend, konteyner içinde değil doğrudan host üzerinde (`dotnet run`) çalışacak şekilde
kurgulandı; sadece bağımlı servisler (veritabanı, önbellek, izleme yığını) Docker Compose
ile ayağa kalkıyor.

---

## 3. Genel mimari

```
                     ┌─────────────────────┐
                     │   Frontend (statik)  │
                     │  HTML/CSS/JS, :5500   │
                     └──────────┬───────────┘
                                │ fetch() + CSRF token + Authorization header
                                ▼
                     ┌─────────────────────┐
                     │   ErpApi (.NET 10)   │   ← host üzerinde, dotnet run, :5077
                     │  Controllers/         │
                     │  Middleware/           │
                     │  Services/             │
                     └───┬────────────┬──────┘
                         │            │
                 EF Core │            │ StackExchange.Redis
                         ▼            ▼
                 ┌──────────────┐ ┌──────────────┐
                 │ PostgreSQL 16 │ │   Redis 7     │  ← Docker Compose
                 └──────────────┘ └──────────────┘

                         │ Serilog (log)      │ /metrics (pull)
                         ▼                     ▼
                 ┌──────────────┐     ┌──────────────┐
                 │     Loki      │     │  Prometheus   │  ← Docker Compose
                 └──────┬───────┘     └──────┬───────┘
                        └───────────┬─────────┘
                                    ▼
                             ┌────────────┐
                             │  Grafana    │  ← Docker Compose, :3000
                             └────────────┘
```

---

## 4. Gelişim süreci — baştan sona neler yapıldı

Commit geçmişi projenin doğal bir kronolojisini veriyor; her aşamada eklenen şeyleri ve
gerekçelerini aşağıda anlatıyorum.

### 4.1 Temel iskelet ve ürün/satış yönetimi

Proje, `ErpApi` adında bir ASP.NET Core Web API projesi olarak başladı. PostgreSQL'e EF Core
ile bağlanan bir `AppDbContext`, `Product` modeli ve `ProductsController` ile temel
CRUD (oluştur/oku/güncelle/sil) işlevleri kuruldu. Ardından `Sale` modeli ve
`SalesController` eklenerek **satış geçmişi** özelliği geldi: bir satış oluşturulduğunda
ilgili ürünün stok miktarı otomatik düşülüyor, yetersiz stok durumunda istek reddediliyor.

Docker Compose ile Postgres ve Redis konteynerleri tanımlandı; ilk sürümlerde Docker
kontrolünün/başlatma sırasının nerede yapılacağı üzerinde ayarlamalar yapıldı
(`docker kontrolun yeri degisti` commit'i).

### 4.2 Kullanıcı sistemi ve kimlik doğrulama

`User` modeli ve `UsersController` eklenerek **çoklu kullanıcı** desteği geldi:

- **Kayıt (`/api/users/register`):** Kullanıcı adı benzersizliği kontrol edilir, parola
  **Argon2id** ile (rastgele salt + yüksek bellek/iterasyon parametreleriyle) hashlenip
  saklanır — parolanın kendisi hiçbir yerde açık tutulmaz.
- **Giriş (`/api/users/login`):** Başarılı girişte Redis'te rastgele 32 byte'lık bir
  **session token** üretilip `session:{token}` anahtarı altında (UserId, Username,
  mutlak bitiş zamanı) saklanır. Oturumun iki farklı zaman aşımı sınırı var:
  - **Hareketsizlik (sliding) sınırı — 5 dakika:** her geçerli istekte `AuthHelper`
    TTL'yi yeniler.
  - **Mutlak sınır — 2 saat:** kullanıcı sürekli aktif olsa bile oturum en fazla 2 saat
    sonra biter.
- **Ürünler/satışlar kullanıcıya özel:** her `Product`'a bir `UserId` eklendi
  (`AddUserIdToProduct` migration'ı), controller'lar sorgularını hep
  `.Where(p => p.UserId == userId)` ile filtreliyor — bir kullanıcı başka bir kullanıcının
  verisini asla göremiyor/değiştiremiyor.

Migration geçmişi (`InitialCreate` → `AddSaleModel` → `AddUserModel` →
`AddUserModelV2` → `AddUserIdToProduct`) bu adımların sırasını EF Core tarafında da
yansıtıyor.

### 4.3 Kaba kuvvet (brute-force) koruması

Login uç noktasına Redis tabanlı bir **rate limiting** mekanizması eklendi:

- Kullanıcı adı başına en fazla **5** başarısız deneme, IP başına en fazla **20**;
  aşıldığında **15 dakikalık** kilit devreye giriyor (HTTP 429 döner).
- Böylece hem "tek bir hesabı zorlama" hem de "aynı IP'den birçok hesabı deneme" (credential
  stuffing) senaryolarına karşı ayrı ayrı koruma var.
- Şifre karşılaştırması **zamanlama saldırılarına (timing attack)** karşı
  `CryptographicOperations.FixedTimeEquals` ile sabit sürede yapılıyor
  (`FixedTimeEquals methodu eklendi` commit'i) — normal `==` karşılaştırması, doğru
  parolaya ne kadar "yakın" olunduğunu ölçülen süre üzerinden sızdırabilirdi.

### 4.4 Redis önbellekleme ayarları ve arayüz güncellemesi

Redis bağlantı/caching ayarları üretim ortamına daha uygun hale getirildi
(`Redis caching ayarları güncellendi`). Aynı dönemde frontend tarafında arayüz
güncellemeleri yapıldı (`arayuz guncellendi`) — dashboard ve ürün sayfaları daha
kullanılabilir hale getirildi.

Git geçmişinde yanlışlıkla iz bırakmış `bin/`, `obj/` gibi derleme çıktıları temizlendi ve
kapsamlı bir `.gitignore` eklendi (`Git önbelleği temizlendi ve gitignore kuralları
uygulandı`).

### 4.5 İzleme (monitoring) altyapısı

Bu, projenin en kapsamlı tekil eklentilerinden biri oldu (`Telemeteri verileri version1`,
`Izleme altyapisi ve dashboard iyilestirmeleri`). Detayları [docs/MONITORING.md](MONITORING.md)
içinde ayrıca belgelendi; özetle:

- **Prometheus:** backend'in `/metrics` endpoint'ini 5 saniyede bir "çekerek" (pull) sayısal
  metrikleri toplar — aktif oturum sayısı, login başarı/başarısızlık oranı, endpoint bazlı
  istek sayısı/süresi gibi.
- **Serilog + Loki:** her önemli olay ("kullanıcı X ürün Y'yi sildi", "kullanıcı Z giriş
  yaptı") yapılandırılmış log satırı olarak hem konsola hem Loki'ye yazılır.
- **Grafana:** ikisini de datasource olarak okuyup "ERP Genel Bakış" adında, 5 panelli
  (aktif oturum sayısı, login denemeleri, endpoint istek oranı, audit log akışı, kullanıcı
  bazlı işlem sayısı) hazır bir dashboard sunar; provisioning dosyaları sayesinde Grafana her
  açıldığında bunlar elle eklenmeden otomatik yüklenir.
- **Cardinality-güvenli tasarım:** kullanıcı adı/IP gibi "sınırsız çeşitlilikte" veriler asla
  Prometheus etiketi yapılmadı, bu tür veriler yalnızca Loki'deki log metninde tutuluyor —
  aksi halde Prometheus'un belleği/diski zamanla şişerdi.
- **`SessionExpiryWatcher`:** Redis'in *keyspace notification* özelliğini dinleyen bir
  arka plan servisi (`IHostedService`). Bir oturum kullanıcı hiç `logout` yapmadan,
  sadece zaman aşımından (TTL) kendiliğinden silinirse bile bunu yakalayıp hem audit
  logu yazıyor hem de aktif oturum sayacını doğru şekilde düşürüyor — bu olmasaydı sayaç
  zamanla gerçek değerden sapardı.
- Bu izleme sistemi sayesinde geliştirme sırasında **gerçek bir performans sorunu**
  (Argon2 parametrelerinden kaynaklanan zaman zaman 1-2 saniyeye çıkan login gecikmesi)
  somut verilerle teşhis edilebildi.

### 4.6 Konum tespiti (geolocation)

`GeoLocationService` eklendi (`Geolocation Ipden çekiliyor`): başarılı bir girişte, kullanıcının
IP adresinden **ip-api.com** servisine sorgu atılıp yaklaşık şehir/ülke bilgisi audit
logunda tutuluyor. Önemli tasarım kararları:

- Özel/loopback IP'ler (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `127.0.0.1` vb.)
  için dış servise hiç sorgu atılmıyor, direkt "Yerel ağ" yazılıyor.
- Dış servis çağrısı **2 saniyelik zaman aşımıyla** sınırlı ve login yanıtını
  **bloklamıyor** — arka planda, ayrı bir DI scope'unda (`IServiceScopeFactory` ile)
  çalıştırılıyor. Böylece ip-api.com yavaşlasa/erişilemez olsa bile kullanıcı giriş
  yapmaya devam edebiliyor.

### 4.7 Gizli bilgilerin repodan çıkarılması

Veritabanı ve Grafana şifreleri gibi hassas değerler koddan/`appsettings.json`'dan
çıkarılıp `.env` (Docker servisleri için, `.gitignore`'da) ve `dotnet user-secrets`
(backend connection string için, repo dışında tutulur) mekanizmalarına taşındı
(`Gizli bilgiler repodan çıkarıldı`). `.env.example` dosyası, hangi değişkenlerin
tanımlanması gerektiğini gösteren şablon olarak repoda kalıyor.

### 4.8 Tema ve CSRF koruması

Son aşamada iki bağımsız özellik birlikte geldi (`Siyah/beyaz tema seçeneği eklendi`,
`Tema ve CSRF çerezleri eklendi`):

- **Karanlık/aydınlık tema:** `theme.css` + `theme.js` ile, tercih bir çerezde
  (`erp-theme`) saklanıp sayfa açıldığında sistem tercihine (`prefers-color-scheme`)
  veya kullanıcının önceki seçimine göre uygulanıyor.
- **CSRF koruması:** `CsrfMiddleware.cs`, **double-submit cookie** yöntemini uyguluyor.
  Her ziyaretçiye `csrf_token` adında (HttpOnly *olmayan*, çünkü frontend JS'in okuyup
  header'a kopyalaması gerekiyor) bir çerez veriliyor; durum değiştiren her istekte
  (`POST`/`PUT`/`DELETE`) bu çerezin değeri ile `X-CSRF-Token` header'ındaki değer sabit
  sürede (`FixedTimeEquals`) karşılaştırılıyor. Başka bir siteden atılan sahte bir istek
  çerezi otomatik taşısa da, aynı-origin kısıtlaması yüzünden onu okuyup header'a
  kopyalayamaz — bu yüzden yöntem işe yarıyor. Frontend tarafında `csrf.js` bu akışı
  yönetiyor ve login/register gibi ilk isteğin doğrudan `POST` olduğu sayfalarda
  `/api/csrf-token` ile önce çerezi "ısındırıyor".

---

## 5. Backend mimarisinin öne çıkan noktaları

- **Katmanlı klasör yapısı:** `Controllers/` (API uçları), `Models/` (veri modelleri),
  `Data/` (EF Core `DbContext`), `Services/` (iş mantığı yardımcıları — parola hashleme,
  auth, geolocation, metrik, oturum izleme), `Middleware/` (CSRF), `Migrations/`
  (EF Core migration geçmişi).
- **Header tabanlı kimlik doğrulama:** JWT yerine bilinçli olarak basit bir
  **opaque session token** (`Authorization` header'ında taşınan rastgele değer, Redis'te
  saklanan durum) tercih edildi; `AuthHelper.GetUserIdAsync` her istekte token'ı
  doğrulayıp kullanıcı kimliğini döndürüyor.
- **CORS:** Frontend farklı bir porttan servis edildiği için, `AllowCredentials()` ile
  birlikte açık origin listesi (`AllowedOrigins` config) tanımlı — credential taşıyan
  isteklerde tarayıcılar wildcard origin'e izin vermiyor.
- **HTTPS yönlendirme, Swagger/OpenAPI** varsayılan olarak açık.
- **Veri izolasyonu:** Tüm controller'lar, dönen/değiştirilen verinin `UserId`'sinin
  o anki oturumun kullanıcısıyla eşleştiğini kontrol ediyor (çok kiracılı güvenlik).

---

## 6. Frontend mimarisinin öne çıkan noktaları

Frontend kasıtlı olarak **framework'süz**: `login.html`, `register.html`, `dashboard.html`,
`products.html` sayfaları ve ortak `theme.css`/`theme.js`/`csrf.js` dosyalarından oluşuyor.
Sayfalar backend'e doğrudan `fetch()` ile, `credentials: "include"` (çerezlerin gidip
gelmesi için) ve gerektiğinde `X-CSRF-Token` + `Authorization` header'larıyla istek atıyor.
`index.html` sadece `login.html`'e yönlendiren bir giriş noktası.

---

## 7. Güvenlik önlemlerinin özeti

Proje boyunca eklenen güvenlik katmanları tek yerde toplanırsa:

1. Argon2id ile parola hashleme (yüksek bellek/iterasyon parametreleriyle brute-force'u
   zorlaştırma).
2. Sabit-süreli (constant-time) parola ve CSRF token karşılaştırması — zamanlama
   saldırılarına karşı.
3. Kullanıcı ve IP bazlı giriş denemesi kilitleme (rate limiting).
4. Oturumlar için hem hareketsizlik hem mutlak zaman aşımı.
5. Double-submit cookie ile CSRF koruması.
6. Kullanıcı bazlı veri izolasyonu (bir kullanıcı başkasının verisine erişemez).
7. Hassas bilgilerin (DB/Grafana şifreleri) koddan/repodan çıkarılıp `.env` ve
   `user-secrets`'a taşınması.
8. Redis'e erişilemediğinde "güvenli taraf" davranışı: kimlik doğrulanamaz kabul edilip
   401 dönülür (fail-closed).

---

## 8. Bilinen sınırlamalar ve sonraki adımlar

`docs/MONITORING.md` içinde izleme sistemine özgü sınırlamalar (retention ayarlanmadı,
alerting yok, Grafana admin şifresi geliştirme ortamı için basit tutuldu vb.) zaten
belgelendi. Proje genelinde, ileride değerlendirilebilecek başlıklar:

- JWT/refresh-token gibi daha standart bir kimlik doğrulama şemasına geçiş (şu an bilinçli
  olarak basit tutulan opaque token yaklaşımı yerine).
- Otomatik test kapsamının eklenmesi (şu an repo içinde ayrı bir test projesi yok).
- Grafana Alerting ile başarısız giriş patlaması gibi durumlarda otomatik bildirim.
- Argon2 parametrelerinin, çalıştığı ortamın kaynak kısıtlarına göre yeniden ayarlanması.

---

*Bu yazı, git commit geçmişi ve mevcut kod tabanı incelenerek 25.08.2026 tarihinde
hazırlanmıştır.*
