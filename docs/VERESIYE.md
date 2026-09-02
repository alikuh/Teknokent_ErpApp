# Dijital Veresiye Defteri — kurulum ve doğrulama

Bu sürüm, eski "ürün ekle/sil/sat" modelini bir **hayvan yemi dükkânı veresiye
takip sistemine** dönüştürür. Altyapı (Redis oturum, CSRF, Argon2, Serilog→Loki,
Prometheus, health checks) değişmedi.

## 1. Şema güncellemesi (zorunlu)

`Sale` tablosu düşer; `Product` değişir; `Customers`, `Receipts`, `ReceiptLines`,
`Payments` eklenir. Tek migration: `VeresiyeModel`.

```bash
docker compose up -d postgres redis
cd backend/ErpApi
dotnet ef database update
```

> Bağlantı şifresi `dotnet user-secrets` içinde (bkz. README). Tasarım zamanı
> `AppDbContextFactory` bu ayarları ve `appsettings*.json`'ı okur.

## 2. Çalıştırma

```bash
cd backend/ErpApi
dotnet run           # http://localhost:5077  (frontend'i de servis eder)
```

## 3. Örnek veri

`ASPNETCORE_ENVIRONMENT=Development` iken sol menüdeki **"Örnek veriyi yükle"**
butonu (veya `POST /api/dev/seed`) oturum açan kullanıcının defterini ~60 müşteri,
13 ürün, ~220 fiş, ~90 tahsilatla doldurur. `POST /api/dev/reset` temizler.
Prod'da bu uçlar 404 döner.

## 4. Uçtan uca kontrol listesi

1. `/register` → `/login` → **Panel**'e düş (4 KPI + borçlular + son hareketler + kritik stok).
2. **Müşteriler** → "Yeni müşteri" → kayıt listeye düşer, arama/sayfalama çalışır.
3. **Ürün & Stok** → ürün ekle/düzenle, `+`/`−` ile stok, sil (geçmiş fiş etkilenmez).
4. **Yeni Satış** → müşteri + satırlar + ödeme şekli (Veresiye/Nakit/Kart) → "Fişi kaydet".
   - Stoktan fazla miktar → uyarı çıkar ama fiş **yine kaydedilir**, stok 0'da kalır.
5. **Veresiye Defteri** → yalnız kapanmamış veresiye fişler; yaş filtresi + arama; "Kalan" sütunu FIFO'ya göre.
6. **Tahsilat** → müşteri + tutar ("Tüm bakiyeyi yaz") + yol → kaydet; son tahsilatlar tablosu.
7. **Müşteri detayı** (satır tıkla) → hesap ekstresi + yürüyen bakiye, not kaydet, en çok aldığı ürünler.
8. **Raporlar** → aylık satış/tahsilat, alacak yaşlandırma, en çok satan ürün, köye göre alacak.
9. CSRF: `X-CSRF-Token` başlığı olmadan `POST /api/receipts` → 403.

## 5. Testler

```bash
cd backend/ErpApi.Tests
dotnet test                 # PasswordHasher (8) + LedgerService (12)
dotnet stryker              # opsiyonel: PasswordHasher + LedgerService mutasyon skoru
```

## 6. API özeti

| Alan | Uçlar |
|---|---|
| Müşteriler | `GET/POST /api/customers`, `GET/PUT/DELETE /api/customers/{id}` |
| Ürünler | `GET/POST /api/products`, `PUT/DELETE /api/products/{id}`, `POST /api/products/{id}/stock-adjust` |
| Fişler | `GET /api/receipts` (açık defter), `GET /api/receipts/{id}`, `POST /api/receipts` |
| Tahsilat | `GET/POST /api/payments` |
| Panel | `GET /api/dashboard/{summary,top-debtors,recent-movements,low-stock}` |
| Raporlar | `GET /api/reports/{monthly,aging,top-products,by-village}` |
| Dev | `POST /api/dev/{seed,reset}` (yalnız Development) |

## 7. Ayar

`appsettings.json` → `Erp:CriticalStockThreshold` (varsayılan 25): ürünün kendi
eşiği (`CriticalStockThreshold`) boşsa kullanılır.
