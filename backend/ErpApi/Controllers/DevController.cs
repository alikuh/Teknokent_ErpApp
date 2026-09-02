using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

// SADECE Development ortamında çalışır. Demo/geliştirme için oturum açan
// kullanıcının defterini örnek veriyle doldurur veya temizler.
[ApiController]
[Route("api/[controller]")]
public class DevController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DevController> _logger;

    public DevController(
        AppDbContext context,
        IConnectionMultiplexer redis,
        IWebHostEnvironment env,
        ILogger<DevController> logger)
    {
        _context = context;
        _redis = redis;
        _env = env;
        _logger = logger;
    }

    private static readonly string[] First =
    {
        "Mehmet", "Ahmet", "Hasan", "Hüseyin", "İbrahim", "Ali", "Mustafa", "Osman",
        "Ramazan", "Yusuf", "Süleyman", "Bekir", "Halil", "Recep", "Kadir", "Fatma",
        "Ayşe", "Emine", "Hatice", "Zeynep", "Şerife", "Necati", "Kemal", "Yılmaz"
    };

    private static readonly string[] Last =
    {
        "Doğan", "Yıldız", "Kaya", "Demir", "Çelik", "Şahin", "Yılmaz", "Aydın",
        "Öztürk", "Arslan", "Koç", "Kurt", "Polat", "Erdem", "Bulut", "Çakır",
        "Toprak", "Güneş", "Sarı", "Ekinci", "Karataş", "Uysal", "Bozkurt", "Aksoy"
    };

    private static readonly string[] Villages =
    {
        "Çayırlı", "Karaağaç", "Yenidoğan", "Söğütlü", "Hacıbey", "Kızılcaören",
        "Akpınar", "Dereköy", "Taşpınar", "Gökçeler", "Merkez / Sanayi", "Beşpınar"
    };

    private static readonly (string Name, string Unit, decimal Price, decimal Stock, decimal? Critical)[] Products =
    {
        ("Süt Yemi %19 Pelet", "25 kg çuval", 385m, 168m, 25m),
        ("Besi Yemi %16", "50 kg çuval", 610m, 92m, 20m),
        ("Buzağı Başlangıç Yemi", "25 kg çuval", 520m, 34m, 25m),
        ("Kuzu Büyütme Yemi", "25 kg çuval", 425m, 57m, 25m),
        ("Yumurta Tavuk Yemi", "25 kg çuval", 340m, 76m, 25m),
        ("Arpa Kırma", "kg", 12.5m, 3400m, 500m),
        ("Mısır Kırma", "kg", 14m, 2150m, 500m),
        ("Buğday Kepeği", "30 kg çuval", 275m, 61m, 20m),
        ("Ayçiçek Küspesi", "50 kg çuval", 890m, 18m, 15m),
        ("Yonca Balya", "balya", 210m, 240m, 40m),
        ("Saman Balya", "balya", 95m, 410m, 40m),
        ("Tuz Yalama Taşı", "10 kg", 165m, 22m, 10m),
        ("Mineral Vitamin Katkı", "5 kg kova", 480m, 14m, 8m)
    };

    // POST: api/dev/seed
    [HttpPost("seed")]
    public async Task<ActionResult> Seed([FromHeader(Name = "Authorization")] string? token)
    {
        if (!_env.IsDevelopment()) return NotFound();

        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        await WipeAsync(userId.Value);

        var rng = new Random(20260901);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var products = Products.Select(p => new Product
        {
            UserId = userId.Value,
            Name = p.Name,
            Unit = p.Unit,
            Price = p.Price,
            Stock = p.Stock,
            CriticalStockThreshold = p.Critical,
            CreatedAt = DateTime.UtcNow
        }).ToList();
        _context.Products.AddRange(products);

        var customers = new List<Customer>();
        for (var i = 0; i < 60; i++)
        {
            customers.Add(new Customer
            {
                UserId = userId.Value,
                Name = $"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}",
                Village = Villages[rng.Next(Villages.Length)],
                Phone = $"0{530 + rng.Next(20)} {100 + rng.Next(900)} {10 + rng.Next(90)} {10 + rng.Next(90)}",
                Note = i < 8 ? "Hasat sonrası kapatır." : null,
                CreatedAt = DateTime.UtcNow.AddDays(-200 - rng.Next(600))
            });
        }
        _context.Customers.AddRange(customers);
        await _context.SaveChangesAsync();

        var receipts = new List<Receipt>();
        for (var i = 0; i < 220; i++)
        {
            // Aktif müşteriler listenin başında yoğunlaşsın.
            var poolSize = rng.NextDouble() < 0.78 ? Math.Min(24, customers.Count) : customers.Count;
            var customer = customers[rng.Next(poolSize)];
            var date = today.AddDays(-rng.Next(180));
            var type = rng.NextDouble() < 0.66 ? ReceiptType.Veresiye
                : (rng.NextDouble() < 0.7 ? ReceiptType.Nakit : ReceiptType.Kart);

            var receipt = new Receipt
            {
                UserId = userId.Value,
                CustomerId = customer.Id,
                Date = date,
                Type = type,
                CreatedAt = DateTime.UtcNow.AddDays(-rng.Next(180))
            };

            var lineCount = 1 + rng.Next(3);
            for (var j = 0; j < lineCount; j++)
            {
                var product = products[rng.Next(products.Count)];
                var qty = product.Unit == "kg" ? (5 + rng.Next(20)) * 10 : 1 + rng.Next(8);
                receipt.Lines.Add(new ReceiptLine
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Quantity = qty,
                    UnitPrice = product.Price,
                    LineTotal = product.Price * qty
                });
            }
            receipt.Total = receipt.Lines.Sum(l => l.LineTotal);
            receipts.Add(receipt);
        }
        _context.Receipts.AddRange(receipts);

        var payments = new List<Payment>();
        for (var i = 0; i < 90; i++)
        {
            var customer = customers[rng.Next(Math.Min(24, customers.Count))];
            payments.Add(new Payment
            {
                UserId = userId.Value,
                CustomerId = customer.Id,
                Date = today.AddDays(-rng.Next(175)),
                Amount = Math.Round((300 + (decimal)rng.NextDouble() * 5200) / 10) * 10,
                Method = rng.NextDouble() < 0.6 ? "Nakit" : (rng.NextDouble() < 0.5 ? "Havale" : "Kart"),
                Note = rng.NextDouble() < 0.25 ? "Kısmi ödeme" : null
            });
        }
        _context.Payments.AddRange(payments);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} örnek veri yükledi: {Customers} müşteri, {Receipts} fiş, {Payments} tahsilat",
            userId, customers.Count, receipts.Count, payments.Count);

        return Ok(new { customers = customers.Count, products = products.Count, receipts = receipts.Count, payments = payments.Count });
    }

    // POST: api/dev/reset
    [HttpPost("reset")]
    public async Task<ActionResult> Reset([FromHeader(Name = "Authorization")] string? token)
    {
        if (!_env.IsDevelopment()) return NotFound();

        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        await WipeAsync(userId.Value);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} defterini sıfırladı.", userId);
        return Ok(new { message = "Defter sıfırlandı." });
    }

    private async Task WipeAsync(int userId)
    {
        // ReceiptLine -> Receipt cascade; önce fiş/tahsilat, sonra müşteri/ürün.
        var receiptIds = await _context.Receipts.Where(r => r.UserId == userId).Select(r => r.Id).ToListAsync();
        _context.ReceiptLines.RemoveRange(_context.ReceiptLines.Where(l => receiptIds.Contains(l.ReceiptId)));
        _context.Receipts.RemoveRange(_context.Receipts.Where(r => r.UserId == userId));
        _context.Payments.RemoveRange(_context.Payments.Where(p => p.UserId == userId));
        _context.Customers.RemoveRange(_context.Customers.Where(c => c.UserId == userId));
        _context.Products.RemoveRange(_context.Products.Where(p => p.UserId == userId));
        await _context.SaveChangesAsync();
    }
}
