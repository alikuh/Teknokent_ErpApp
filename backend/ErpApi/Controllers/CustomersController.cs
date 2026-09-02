using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CustomersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CustomersController> _logger;
    private readonly LedgerRepository _ledgerRepo;
    private readonly LedgerService _ledger;

    public CustomersController(
        AppDbContext context,
        IConnectionMultiplexer redis,
        ILogger<CustomersController> logger,
        LedgerRepository ledgerRepo,
        LedgerService ledger)
    {
        _context = context;
        _redis = redis;
        _logger = logger;
        _ledgerRepo = ledgerRepo;
        _ledger = ledger;
    }

    public class CustomerRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Village { get; set; }
        public string? Phone { get; set; }
        public string? Note { get; set; }
    }

    // GET: api/customers?q=&page=0&pageSize=12
    [HttpGet]
    public async Task<ActionResult> GetCustomers(
        [FromQuery] string? q,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (page < 0) page = 0;
        if (pageSize <= 0) pageSize = 12;
        if (pageSize > 200) pageSize = 200;

        var customers = await _context.Customers
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            customers = customers
                .Where(c => Contains(c.Name, needle)
                            || Contains(c.Village, needle)
                            || Contains(c.Phone, needle))
                .ToList();
        }

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value);
        var balances = _ledger.GetBalances(receipts, payments);

        var lastMove = LastMovementByCustomer(receipts, payments);
        var receiptCounts = receipts
            .GroupBy(r => r.CustomerId)
            .ToDictionary(g => g.Key, g => g.Count());

        var total = customers.Count;
        var items = customers
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Village,
                c.Phone,
                c.Note,
                c.CreatedAt,
                balance = balances.GetValueOrDefault(c.Id),
                lastMovement = lastMove.TryGetValue(c.Id, out var d) ? d : (DateOnly?)null,
                receiptCount = receiptCounts.GetValueOrDefault(c.Id)
            });

        var debtorCount = balances.Count(b => b.Value > 0);

        return Ok(new { items, total, debtorCount });
    }

    // GET: api/customers/5
    [HttpGet("{id}")]
    public async Task<ActionResult> GetCustomer(int id, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var customer = await _context.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (customer == null) return NotFound();

        var (receipts, payments) = await _ledgerRepo.LoadAsync(userId.Value, includeLines: true);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var balances = _ledger.GetBalances(receipts, payments);
        var openReceipts = _ledger.GetOpenReceipts(receipts, payments);
        var statement = _ledger.GetStatement(receipts, payments, id);

        var custReceipts = receipts.Where(r => r.CustomerId == id).ToList();
        var custPayments = payments.Where(p => p.CustomerId == id).ToList();

        var topProducts = custReceipts
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.ProductName)
            .Select(g => new { name = g.Key, qty = g.Sum(x => x.Quantity) })
            .OrderByDescending(x => x.qty)
            .Take(5)
            .ToList();

        var balance = balances.GetValueOrDefault(id);
        var lastPayment = custPayments
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .FirstOrDefault();

        return Ok(new
        {
            customer.Id,
            customer.Name,
            customer.Village,
            customer.Phone,
            customer.Note,
            customer.CreatedAt,
            kpis = new
            {
                balance,
                totalPurchases = custReceipts.Sum(r => r.Total),
                openReceiptCount = openReceipts.Count(o => o.Receipt.CustomerId == id),
                lastPaymentDate = lastPayment?.Date
            },
            statement = statement.Select(s => new
            {
                date = s.Date,
                receiptId = s.ReceiptId,
                kind = s.Kind,
                detail = s.Detail,
                debit = s.Debit,
                credit = s.Credit,
                running = s.Running
            }),
            topProducts
        });
    }

    // POST: api/customers
    [HttpPost]
    public async Task<ActionResult> CreateCustomer(CustomerRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Ad soyad gerekli.");

        var customer = new Customer
        {
            UserId = userId.Value,
            Name = request.Name.Trim(),
            Village = Clean(request.Village),
            Phone = Clean(request.Phone),
            Note = Clean(request.Note),
            CreatedAt = DateTime.UtcNow
        };

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} yeni müşteri ekledi: {CustomerId} - {CustomerName}", userId, customer.Id, customer.Name);

        return CreatedAtAction(nameof(GetCustomer), new { id = customer.Id }, new
        {
            customer.Id,
            customer.Name,
            customer.Village,
            customer.Phone,
            customer.Note,
            customer.CreatedAt
        });
    }

    // PUT: api/customers/5
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCustomer(int id, CustomerRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var customer = await _context.Customers
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (customer == null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Ad soyad gerekli.");

        customer.Name = request.Name.Trim();
        customer.Village = Clean(request.Village);
        customer.Phone = Clean(request.Phone);
        customer.Note = Clean(request.Note);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} müşteriyi güncelledi: {CustomerId} - {CustomerName}", userId, customer.Id, customer.Name);

        return NoContent();
    }

    // DELETE: api/customers/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCustomer(int id, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var customer = await _context.Customers
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (customer == null) return NotFound();

        var hasHistory = await _context.Receipts.AnyAsync(r => r.CustomerId == id)
            || await _context.Payments.AnyAsync(p => p.CustomerId == id);
        if (hasHistory)
            return Conflict("Fiş veya tahsilat geçmişi olan müşteri silinemez.");

        _context.Customers.Remove(customer);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Kullanıcı {UserId} müşteriyi sildi: {CustomerId} - {CustomerName}", userId, customer.Id, customer.Name);

        return NoContent();
    }

    private static Dictionary<int, DateOnly> LastMovementByCustomer(
        IEnumerable<Receipt> receipts, IEnumerable<Payment> payments)
    {
        var map = new Dictionary<int, DateOnly>();
        foreach (var r in receipts)
            if (!map.TryGetValue(r.CustomerId, out var d) || r.Date > d) map[r.CustomerId] = r.Date;
        foreach (var p in payments)
            if (!map.TryGetValue(p.CustomerId, out var d) || p.Date > d) map[p.CustomerId] = p.Date;
        return map;
    }

    private static bool Contains(string? haystack, string needle)
        => haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
