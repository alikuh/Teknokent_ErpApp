using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly LedgerRepository _ledgerRepo;
    private readonly LedgerService _ledger;
    private readonly decimal _defaultCriticalThreshold;

    public DashboardController(
        AppDbContext context,
        IConnectionMultiplexer redis,
        LedgerRepository ledgerRepo,
        LedgerService ledger,
        IConfiguration configuration)
    {
        _context = context;
        _redis = redis;
        _ledgerRepo = ledgerRepo;
        _ledger = ledger;
        _defaultCriticalThreshold = configuration.GetValue<decimal?>("Erp:CriticalStockThreshold") ?? 25m;
    }

    // GET: api/dashboard/summary
    [HttpGet("summary")]
    public async Task<ActionResult> GetSummary([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var balances = _ledger.GetBalances(receipts, payments);
        var openReceivables = balances.Values.Sum();
        var debtorCount = balances.Count(b => b.Value > 0);

        var todayReceipts = receipts.Where(r => r.Date == today).ToList();
        var monthPayments = payments.Where(p => p.Date >= monthStart).ToList();

        var products = await _context.Products.AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync();
        var criticalStockCount = products.Count(IsCritical);

        return Ok(new
        {
            openReceivables,
            debtorCount,
            todaySales = todayReceipts.Sum(r => r.Total),
            todayReceiptCount = todayReceipts.Count,
            monthCollected = monthPayments.Sum(p => p.Amount),
            monthPaymentCount = monthPayments.Count,
            criticalStockCount
        });
    }

    // GET: api/dashboard/top-debtors?count=8
    [HttpGet("top-debtors")]
    public async Task<ActionResult> GetTopDebtors([FromQuery] int count, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (count <= 0) count = 8;

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value);
        var balances = _ledger.GetBalances(receipts, payments);
        var lastMove = LastMovement(receipts, payments);

        var debtorIds = balances.Where(b => b.Value > 0)
            .OrderByDescending(b => b.Value)
            .Take(count)
            .Select(b => b.Key)
            .ToList();

        var customers = await _context.Customers.AsNoTracking()
            .Where(c => debtorIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        var rows = debtorIds.Select(id =>
        {
            var c = customers.GetValueOrDefault(id);
            return new
            {
                customerId = id,
                name = c?.Name ?? "(silinmiş)",
                village = c?.Village,
                lastMovement = lastMove.TryGetValue(id, out var d) ? d : (DateOnly?)null,
                balance = balances[id]
            };
        });

        return Ok(rows);
    }

    // GET: api/dashboard/recent-movements?count=8
    [HttpGet("recent-movements")]
    public async Task<ActionResult> GetRecentMovements([FromQuery] int count, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (count <= 0) count = 8;

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value);

        var customers = await _context.Customers.AsNoTracking()
            .Where(c => c.UserId == userId)
            .ToDictionaryAsync(c => c.Id);

        var moves = receipts
            .Select(r => new
            {
                date = r.Date,
                order = r.CreatedAt,
                name = customers.GetValueOrDefault(r.CustomerId)?.Name ?? "(silinmiş)",
                kind = r.Type.ToString().ToLowerInvariant(),
                amount = r.Total
            })
            .Concat(payments.Select(p => new
            {
                date = p.Date,
                order = p.CreatedAt,
                name = customers.GetValueOrDefault(p.CustomerId)?.Name ?? "(silinmiş)",
                kind = "tahsilat",
                amount = p.Amount
            }))
            .OrderByDescending(m => m.date)
            .ThenByDescending(m => m.order)
            .Take(count)
            .Select(m => new { m.date, m.name, m.kind, m.amount });

        return Ok(moves);
    }

    // GET: api/dashboard/low-stock
    [HttpGet("low-stock")]
    public async Task<ActionResult> GetLowStock([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var products = await _context.Products.AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync();

        var rows = products
            .Where(IsCritical)
            .OrderBy(p => p.Stock)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Unit,
                p.Stock,
                threshold = p.CriticalStockThreshold ?? _defaultCriticalThreshold
            });

        return Ok(rows);
    }

    private bool IsCritical(Product p)
        => p.Stock < (p.CriticalStockThreshold ?? _defaultCriticalThreshold);

    private static Dictionary<int, DateOnly> LastMovement(
        IEnumerable<Receipt> receipts, IEnumerable<Payment> payments)
    {
        var map = new Dictionary<int, DateOnly>();
        foreach (var r in receipts)
            if (!map.TryGetValue(r.CustomerId, out var d) || r.Date > d) map[r.CustomerId] = r.Date;
        foreach (var p in payments)
            if (!map.TryGetValue(p.CustomerId, out var d) || p.Date > d) map[p.CustomerId] = p.Date;
        return map;
    }
}
