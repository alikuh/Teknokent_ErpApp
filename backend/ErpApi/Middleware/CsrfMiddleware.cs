using System.Security.Cryptography;
using System.Text;

namespace ErpApi.Middleware;

// "Double submit cookie" yontemiyle CSRF korumasi.
//
// Mantik: her ziyaretciye bir csrf_token cerezi verilir (HttpOnly DEGIL,
// cunku frontend JS'in okuyup ayni degeri header'a kopyalamasi gerekiyor).
// Durum degistiren isteklerde (POST/PUT/DELETE...) hem cerezdeki hem
// header'daki deger karsilastirilir; sadece esitse istege izin verilir.
//
// Bunun ise yaramasinin sebebi: baska bir siteden (CSRF saldirisi) atilan
// sahte bir istek, tarayicinin otomatik ekledigi cerezi tasiyabilir ama
// ayni-origin kisitlamasi yuzunden o cerezin degerini okuyup header'a
// kopyalayamaz - dolayisiyla header ya hic olmaz ya da yanlis olur.
public class CsrfMiddleware
{
    public const string CookieName = "csrf_token";
    public const string HeaderName = "X-CSRF-Token";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? cookieToken = context.Request.Cookies[CookieName];

        // Cerez yoksa (ilk ziyaret ya da suresi dolmus) yenisini uretip yaziyoruz.
        if (string.IsNullOrEmpty(cookieToken))
        {
            cookieToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(CookieName, cookieToken, new CookieOptions
            {
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Path = "/",
                MaxAge = TimeSpan.FromDays(1)
            });
        }

        if (!SafeMethods.Contains(context.Request.Method))
        {
            string? headerToken = context.Request.Headers[HeaderName].ToString();

            if (string.IsNullOrEmpty(headerToken) || !TokensMatch(headerToken, cookieToken))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("CSRF doğrulaması başarısız.");
                return;
            }
        }

        await _next(context);
    }

    private static bool TokensMatch(string headerToken, string cookieToken)
    {
        byte[] headerBytes = Encoding.UTF8.GetBytes(headerToken);
        byte[] cookieBytes = Encoding.UTF8.GetBytes(cookieToken);

        // Farkli uzunluktaki dizileri FixedTimeEquals'a vermek istisna firlatir;
        // uzunluk zaten gizli bir bilgi olmadigi icin burada erken donmek guvenli.
        if (headerBytes.Length != cookieBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(headerBytes, cookieBytes);
    }
}
