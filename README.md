# Teknokent_ErpApp

## Kurulum

Şifreler/gizli bilgiler repoya commit edilmez; ilk kurulumda şunları yapman gerekir:

1. **Docker (Postgres, Redis, izleme yığını):**
   ```
   cp .env.example .env
   ```
   `.env` içindeki değerleri kendi şifrenle güncelle, sonra:
   ```
   docker compose up -d
   ```

2. **Backend API (`backend/ErpApi`):** Connection string `appsettings.json`'da şifresiz duruyor; gerçek şifreyi `dotnet user-secrets` ile (repo dışında, kendi makinene) tanımla:
   ```
   cd backend/ErpApi
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=erp_db;Username=erp_user;Password=<.env'deki POSTGRES_PASSWORD>"
   ```
