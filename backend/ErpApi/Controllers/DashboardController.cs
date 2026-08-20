using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;

    public DashboardController(AppDbContext context, IConnectionMultiplexer redis)
    {
        _context = context;
        _redis = redis;
    }

    // GET: api/dashboard/summary
    [HttpGet("summary")]
    public async Task<ActionResult> GetSummary([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var startOfWeek = DateTime.SpecifyKind(today.AddDays(-(int)today.DayOfWeek), DateTimeKind.Utc);
        var startOfMonth = DateTime.SpecifyKind(new DateTime(today.Year, today.Month, 1), DateTimeKind.Utc);

        var salesQuery = _context.Sales.Include(s => s.Product).Where(s => s.Product!.UserId == userId);

        var dailyTotal = await salesQuery.Where(s => s.SoldAt >= today).SumAsync(s => s.TotalPrice);
        var weeklyTotal = await salesQuery.Where(s => s.SoldAt >= startOfWeek).SumAsync(s => s.TotalPrice);
        var monthlyTotal = await salesQuery.Where(s => s.SoldAt >= startOfMonth).SumAsync(s => s.TotalPrice);

        var productsQuery = _context.Products.Where(p => p.UserId == userId);
        var totalProducts = await productsQuery.CountAsync();
        var totalStock = await productsQuery.SumAsync(p => p.StockQuantity);
        var lowStockProducts = await productsQuery.Where(p => p.StockQuantity < 10).CountAsync();

        return Ok(new
        {
            dailySales = dailyTotal,
            weeklySales = weeklyTotal,
            monthlySales = monthlyTotal,
            totalProducts,
            totalStock,
            lowStockProducts
        });
    }

    // GET: api/dashboard/sales-by-day?days=7
    [HttpGet("sales-by-day")]
    public async Task<ActionResult> GetSalesByDay([FromQuery] int days, [FromHeader(Name = "Authorization")] string? token)
    {
        if (days <= 0) days = 7;

        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var startDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-days + 1), DateTimeKind.Utc);

        var sales = await _context.Sales
            .Include(s => s.Product)
            .Where(s => s.Product!.UserId == userId && s.SoldAt >= startDate)
            .ToListAsync();

        var grouped = sales
            .GroupBy(s => s.SoldAt.Date)
            .Select(g => new
            {
                date = g.Key.ToString("yyyy-MM-dd"),
                totalRevenue = g.Sum(s => s.TotalPrice),
                totalQuantity = g.Sum(s => s.Quantity)
            })
            .OrderBy(x => x.date)
            .ToList();

        return Ok(grouped);
    }

    // GET: api/dashboard/top-products?count=5
    [HttpGet("top-products")]
    public async Task<ActionResult> GetTopProducts([FromQuery] int count, [FromHeader(Name = "Authorization")] string? token)
    {
        if (count <= 0) count = 5;

        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var topProducts = await _context.Sales
            .Include(s => s.Product)
            .Where(s => s.Product!.UserId == userId)
            .GroupBy(s => new { s.ProductId, s.Product!.Name })
            .Select(g => new
            {
                productId = g.Key.ProductId,
                productName = g.Key.Name,
                totalSold = g.Sum(s => s.Quantity),
                totalRevenue = g.Sum(s => s.TotalPrice)
            })
            .OrderByDescending(x => x.totalSold)
            .Take(count)
            .ToListAsync();

        return Ok(topProducts);
    }
}