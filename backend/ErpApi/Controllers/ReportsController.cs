using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly LedgerRepository _ledgerRepo;
    private readonly LedgerService _ledger;

    public ReportsController(
        AppDbContext context,
        IConnectionMultiplexer redis,
        LedgerRepository ledgerRepo,
        LedgerService ledger)
    {
        _context = context;
        _redis = redis;
        _ledgerRepo = ledgerRepo;
        _ledger = ledger;
    }

    // GET: api/reports/monthly?months=6
    [HttpGet("monthly")]
    public async Task<ActionResult> GetMonthly([FromQuery] int months, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (months <= 0) months = 6;
        if (months > 24) months = 24;

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var rows = new List<object>();
        for (var i = months - 1; i >= 0; i--)
        {
            var monthDate = new DateOnly(today.Year, today.Month, 1).AddMonths(-i);
            var monthEnd = monthDate.AddMonths(1);

            rows.Add(new
            {
                month = monthDate.ToString("yyyy-MM"),
                sales = receipts.Where(r => r.Date >= monthDate && r.Date < monthEnd).Sum(r => r.Total),
                collected = payments.Where(p => p.Date >= monthDate && p.Date < monthEnd).Sum(p => p.Amount)
            });
        }

        return Ok(rows);
    }

    // GET: api/reports/aging
    [HttpGet("aging")]
    public async Task<ActionResult> GetAging([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var open = _ledger.GetOpenReceipts(receipts, payments);
        var aging = _ledger.GetAging(open, today);

        return Ok(aging.Select(a => new { band = a.Band, count = a.Count, amount = a.Amount }));
    }

    // GET: api/reports/top-products?count=8
    [HttpGet("top-products")]
    public async Task<ActionResult> GetTopProducts([FromQuery] int count, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (count <= 0) count = 8;

        var lines = await _context.ReceiptLines
            .AsNoTracking()
            .Where(l => l.Receipt!.UserId == userId)
            .GroupBy(l => l.ProductName)
            .Select(g => new
            {
                name = g.Key,
                qty = g.Sum(x => x.Quantity),
                total = g.Sum(x => x.LineTotal)
            })
            .OrderByDescending(x => x.total)
            .Take(count)
            .ToListAsync();

        return Ok(lines);
    }

    // GET: api/reports/by-village
    [HttpGet("by-village")]
    public async Task<ActionResult> GetByVillage([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value);
        var balances = _ledger.GetBalances(receipts, payments);

        var debtorIds = balances.Where(b => b.Value > 0).Select(b => b.Key).ToList();

        var customers = await _context.Customers.AsNoTracking()
            .Where(c => debtorIds.Contains(c.Id))
            .ToListAsync();

        var rows = customers
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Village) ? "—" : c.Village!)
            .Select(g => new
            {
                village = g.Key,
                amount = g.Sum(c => balances.GetValueOrDefault(c.Id))
            })
            .OrderByDescending(x => x.amount)
            .ToList();

        return Ok(rows);
    }
}
