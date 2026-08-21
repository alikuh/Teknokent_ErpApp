using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SalesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SalesController> _logger;

    public SalesController(AppDbContext context, IConnectionMultiplexer redis, ILogger<SalesController> logger)
    {
        _context = context;
        _redis = redis;
        _logger = logger;
    }

    public class CreateSaleRequest
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    // GET: api/sales
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Sale>>> GetSales([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        return await _context.Sales
            .Include(s => s.Product)
            .Where(s => s.Product!.UserId == userId)
            .ToListAsync();
    }

    // POST: api/sales
    [HttpPost]
    public async Task<ActionResult<Sale>> CreateSale(CreateSaleRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var product = await _context.Products.FindAsync(request.ProductId);

        if (product == null || product.UserId != userId)
        {
            return NotFound("Ürün bulunamadı.");
        }

        if (product.StockQuantity < request.Quantity)
        {
            return BadRequest($"Yetersiz stok. Mevcut stok: {product.StockQuantity}");
        }

        var sale = new Sale
        {
            ProductId = product.Id,
            Quantity = request.Quantity,
            UnitPrice = product.Price,
            TotalPrice = product.Price * request.Quantity,
            SoldAt = DateTime.UtcNow
        };

        product.StockQuantity -= request.Quantity;
        product.UpdatedAt = DateTime.UtcNow;

        _context.Sales.Add(sale);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Kullanıcı {UserId} satış oluşturdu: satış {SaleId}, ürün {ProductId}, adet {Quantity}, tutar {TotalPrice}",
            userId, sale.Id, sale.ProductId, sale.Quantity, sale.TotalPrice);

        return CreatedAtAction(nameof(GetSales), new { id = sale.Id }, sale);
    }
}