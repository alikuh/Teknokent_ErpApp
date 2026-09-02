using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ProductsController> _logger;
    private readonly decimal _defaultCriticalThreshold;

    public ProductsController(
        AppDbContext context,
        IConnectionMultiplexer redis,
        ILogger<ProductsController> logger,
        IConfiguration configuration)
    {
        _context = context;
        _redis = redis;
        _logger = logger;
        _defaultCriticalThreshold = configuration.GetValue<decimal?>("Erp:CriticalStockThreshold") ?? 25m;
    }

    public class ProductRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Unit { get; set; }
        public decimal Price { get; set; }
        public decimal Stock { get; set; }
        public decimal? CriticalStockThreshold { get; set; }
    }

    public class StockAdjustRequest
    {
        public decimal Delta { get; set; }
    }

    // GET: api/products
    [HttpGet]
    public async Task<ActionResult> GetProducts([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
        var sold30 = await _context.ReceiptLines
            .AsNoTracking()
            .Where(l => l.Receipt!.UserId == userId
                        && l.Receipt.Date >= since
                        && l.ProductId != null)
            .GroupBy(l => l.ProductId!.Value)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Qty);

        return Ok(products.Select(p => ToDto(p, sold30.GetValueOrDefault(p.Id))));
    }

    // GET: api/products/5
    [HttpGet("{id}")]
    public async Task<ActionResult> GetProduct(int id, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var product = await _context.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        if (product == null) return NotFound();

        return Ok(ToDto(product, 0m));
    }

    // POST: api/products
    [HttpPost]
    public async Task<ActionResult> CreateProduct(ProductRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Ürün adı gerekli.");

        var product = new Product
        {
            UserId = userId.Value,
            Name = request.Name.Trim(),
            Unit = string.IsNullOrWhiteSpace(request.Unit) ? "adet" : request.Unit.Trim(),
            Price = request.Price,
            Stock = request.Stock,
            CriticalStockThreshold = request.CriticalStockThreshold,
            CreatedAt = DateTime.UtcNow
        };

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} yeni ürün oluşturdu: {ProductId} - {ProductName}", userId, product.Id, product.Name);

        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, ToDto(product, 0m));
    }

    // PUT: api/products/5
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProduct(int id, ProductRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (product == null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Ürün adı gerekli.");

        product.Name = request.Name.Trim();
        product.Unit = string.IsNullOrWhiteSpace(request.Unit) ? "adet" : request.Unit.Trim();
        product.Price = request.Price;
        product.Stock = request.Stock;
        product.CriticalStockThreshold = request.CriticalStockThreshold;
        product.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} ürünü güncelledi: {ProductId} - {ProductName}", userId, product.Id, product.Name);

        return NoContent();
    }

    // POST: api/products/5/stock-adjust  { delta: +/- }
    [HttpPost("{id}/stock-adjust")]
    public async Task<ActionResult> AdjustStock(int id, StockAdjustRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (product == null) return NotFound();

        product.Stock = Math.Max(0m, product.Stock + request.Delta);
        product.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} stok düzeltti: {ProductId} - {ProductName} ({Delta:+0.##;-0.##}) → {Stock}",
            userId, product.Id, product.Name, request.Delta, product.Stock);

        return Ok(ToDto(product, 0m));
    }

    // DELETE: api/products/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProduct(int id, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (product == null) return NotFound();

        // Fiş satırları ProductId'yi NULL'a çeker (ad/fiyat snapshot kalır),
        // geçmiş fişler bozulmaz.
        _context.Products.Remove(product);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} ürünü sildi: {ProductId} - {ProductName}", userId, product.Id, product.Name);

        return NoContent();
    }

    private object ToDto(Product p, decimal sold30)
    {
        var threshold = p.CriticalStockThreshold ?? _defaultCriticalThreshold;
        return new
        {
            p.Id,
            p.Name,
            p.Unit,
            p.Price,
            p.Stock,
            criticalStockThreshold = p.CriticalStockThreshold,
            critical = p.Stock < threshold,
            sold30
        };
    }
}
