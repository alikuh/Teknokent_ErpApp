using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReceiptsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ReceiptsController> _logger;
    private readonly LedgerRepository _ledgerRepo;
    private readonly LedgerService _ledger;

    public ReceiptsController(
        AppDbContext context,
        IConnectionMultiplexer redis,
        ILogger<ReceiptsController> logger,
        LedgerRepository ledgerRepo,
        LedgerService ledger)
    {
        _context = context;
        _redis = redis;
        _logger = logger;
        _ledgerRepo = ledgerRepo;
        _ledger = ledger;
    }

    public class CreateReceiptRequest
    {
        public int CustomerId { get; set; }
        public DateOnly? Date { get; set; }
        public ReceiptType Type { get; set; } = ReceiptType.Veresiye;
        public string? Note { get; set; }
        public List<Line> Lines { get; set; } = new();

        public class Line
        {
            public int ProductId { get; set; }
            public decimal Quantity { get; set; }
        }
    }

    // GET: api/receipts?q=&ageBand=all|0-30|31-60|60+
    // Kapanmamış (kalanı olan) veresiye fişleri - "Veresiye defteri" ekranı.
    [HttpGet]
    public async Task<ActionResult> GetOpenReceipts(
        [FromQuery] string? q,
        [FromQuery] string? ageBand,
        [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value, includeLines: true);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var open = _ledger.GetOpenReceipts(receipts, payments);

        var customers = await _context.Customers.AsNoTracking()
            .Where(c => c.UserId == userId)
            .ToDictionaryAsync(c => c.Id);

        (int Min, int Max)? band = ageBand switch
        {
            "0-30" => (0, 30),
            "31-60" => (31, 60),
            "60+" => (61, int.MaxValue),
            _ => null
        };

        var needle = q?.Trim();

        var rows = open
            .Select(o =>
            {
                var c = customers.GetValueOrDefault(o.Receipt.CustomerId);
                return new
                {
                    o.Receipt.Id,
                    no = $"F-{o.Receipt.Id}",
                    date = o.Receipt.Date,
                    customerId = o.Receipt.CustomerId,
                    customerName = c?.Name ?? "(silinmiş müşteri)",
                    village = c?.Village,
                    summary = string.Join(", ", o.Receipt.Lines.Select(l => $"{l.ProductName} ×{Trim(l.Quantity)}")),
                    ageDays = LedgerService.AgeInDays(o.Receipt.Date, today),
                    total = o.Receipt.Total,
                    remaining = o.Remaining
                };
            })
            .Where(r => band == null || (r.ageDays >= band.Value.Min && r.ageDays <= band.Value.Max))
            .Where(r => string.IsNullOrEmpty(needle)
                        || r.customerName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || (r.village ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.date)
            .ThenBy(r => r.Id)
            .ToList();

        return Ok(new { items = rows, totalOpen = open.Sum(o => o.Remaining), count = rows.Count });
    }

    // GET: api/receipts/5
    [HttpGet("{id}")]
    public async Task<ActionResult> GetReceipt(int id, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var receipt = await _context.Receipts
            .AsNoTracking()
            .Include(r => r.Lines)
            .Include(r => r.Customer)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

        if (receipt == null) return NotFound();

        return Ok(new
        {
            receipt.Id,
            no = $"F-{receipt.Id}",
            date = receipt.Date,
            type = receipt.Type.ToString(),
            receipt.Note,
            receipt.Total,
            customerId = receipt.CustomerId,
            customerName = receipt.Customer?.Name,
            lines = receipt.Lines.Select(l => new
            {
                l.ProductId,
                l.ProductName,
                l.Quantity,
                l.UnitPrice,
                l.LineTotal
            })
        });
    }

    // POST: api/receipts
    [HttpPost]
    public async Task<ActionResult> CreateReceipt(CreateReceiptRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (request.Lines == null || request.Lines.Count == 0)
            return BadRequest("Fişe en az bir satır ekleyin.");

        var customer = await _context.Customers
            .FirstOrDefaultAsync(c => c.Id == request.CustomerId && c.UserId == userId);
        if (customer == null) return BadRequest("Müşteri bulunamadı.");

        var productIds = request.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _context.Products
            .Where(p => p.UserId == userId && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        var receipt = new Receipt
        {
            UserId = userId.Value,
            CustomerId = customer.Id,
            Date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Type = request.Type,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0)
                return BadRequest("Miktar 0'dan büyük olmalı.");
            if (!products.TryGetValue(line.ProductId, out var product))
                return BadRequest($"Ürün bulunamadı (Id: {line.ProductId}).");

            var lineTotal = product.Price * line.Quantity;
            receipt.Lines.Add(new ReceiptLine
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = line.Quantity,
                UnitPrice = product.Price,
                LineTotal = lineTotal
            });

            // Stok eksiye düşmez ama satış engellenmez (tasarım kuralı).
            product.Stock = Math.Max(0m, product.Stock - line.Quantity);
            product.UpdatedAt = DateTime.UtcNow;
        }

        receipt.Total = receipt.Lines.Sum(l => l.LineTotal);

        _context.Receipts.Add(receipt);
        await _context.SaveChangesAsync();

        AppMetrics.ReceiptsCreatedTotal.WithLabels(receipt.Type.ToString()).Inc();

        _logger.LogInformation(
            "Kullanıcı {UserId} fiş kaydetti: fiş {ReceiptId}, müşteri {CustomerId}, tür {Type}, {LineCount} satır, tutar {Total}",
            userId, receipt.Id, receipt.CustomerId, receipt.Type, receipt.Lines.Count, receipt.Total);

        return CreatedAtAction(nameof(GetReceipt), new { id = receipt.Id }, new
        {
            receipt.Id,
            no = $"F-{receipt.Id}",
            receipt.Total,
            type = receipt.Type.ToString()
        });
    }

    private static string Trim(decimal value)
        => value == Math.Truncate(value) ? ((long)value).ToString() : value.ToString("0.##");
}
