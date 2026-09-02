using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        AppDbContext context,
        IConnectionMultiplexer redis,
        ILogger<PaymentsController> logger)
    {
        _context = context;
        _redis = redis;
        _logger = logger;
    }

    public class CreatePaymentRequest
    {
        public int CustomerId { get; set; }
        public DateOnly? Date { get; set; }
        public decimal Amount { get; set; }
        public string? Method { get; set; }
        public string? Note { get; set; }
    }

    // GET: api/payments?limit=20
    [HttpGet]
    public async Task<ActionResult> GetPayments([FromQuery] int limit, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (limit <= 0) limit = 20;
        if (limit > 200) limit = 200;

        var payments = await _context.Payments
            .AsNoTracking()
            .Include(p => p.Customer)
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.Date)
            .ThenByDescending(p => p.Id)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Date,
                customerId = p.CustomerId,
                customerName = p.Customer!.Name,
                p.Method,
                p.Note,
                p.Amount
            })
            .ToListAsync();

        return Ok(payments);
    }

    // POST: api/payments
    [HttpPost]
    public async Task<ActionResult> CreatePayment(CreatePaymentRequest request, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        if (request.Amount <= 0)
            return BadRequest("Tutar 0'dan büyük olmalı.");

        var customer = await _context.Customers
            .FirstOrDefaultAsync(c => c.Id == request.CustomerId && c.UserId == userId);
        if (customer == null) return BadRequest("Müşteri bulunamadı.");

        var payment = new Payment
        {
            UserId = userId.Value,
            CustomerId = customer.Id,
            Date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Amount = request.Amount,
            Method = string.IsNullOrWhiteSpace(request.Method) ? "Nakit" : request.Method.Trim(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _context.Payments.Add(payment);
        await _context.SaveChangesAsync();

        AppMetrics.PaymentsCreatedTotal.Inc();

        _logger.LogInformation(
            "Kullanıcı {UserId} tahsilat kaydetti: tahsilat {PaymentId}, müşteri {CustomerId}, tutar {Amount}, yöntem {Method}",
            userId, payment.Id, payment.CustomerId, payment.Amount, payment.Method);

        return CreatedAtAction(nameof(GetPayments), new { }, new { payment.Id, payment.Amount });
    }
}
