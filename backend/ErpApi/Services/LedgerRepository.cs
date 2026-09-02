using ErpApi.Data;
using ErpApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ErpApi.Services;

// Cari hesap ekranlarının çoğu (panel, müşteri detayı, veresiye defteri,
// raporlar) aynı iki listeye ihtiyaç duyuyor: kullanıcının tüm fişleri ve
// tahsilatları. Tek yerden çekelim.
public class LedgerRepository
{
    private readonly AppDbContext _db;

    public LedgerRepository(AppDbContext db) => _db = db;

    public async Task<(List<Receipt> Receipts, List<Payment> Payments)> LoadAsync(
        int userId, bool includeLines = false)
    {
        IQueryable<Receipt> receiptsQuery = _db.Receipts
            .AsNoTracking()
            .Where(r => r.UserId == userId);

        if (includeLines)
        {
            receiptsQuery = receiptsQuery.Include(r => r.Lines);
        }

        var receipts = await receiptsQuery.ToListAsync();

        var payments = await _db.Payments
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync();

        return (receipts, payments);
    }
}
