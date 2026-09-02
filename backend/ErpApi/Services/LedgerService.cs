using ErpApi.Models;

namespace ErpApi.Services;

// Cari hesap matematiği tek yerde. Hepsi saf fonksiyon: EF/DbContext'e
// dokunmaz, kendisine verilen koleksiyonlar üzerinde çalışır. Controller'lar
// kullanıcının fiş + tahsilatlarını çekip buraya verir; böylece kolay test edilir.
public class LedgerService
{
    // 0.005 altı bakiyeyi (kayan nokta artığı) 0 kabul et.
    private const decimal Epsilon = 0.005m;

    public record OpenReceipt(Receipt Receipt, decimal Remaining);

    public record AgingBucket(string Band, int Count, decimal Amount);

    public record StatementEntry(
        DateOnly Date,
        int? ReceiptId,
        string Kind,        // "veresiye" | "nakit" | "kart" | "tahsilat"
        string Detail,
        decimal Debit,      // borç (veresiye fiş)
        decimal Credit,     // alacak (tahsilat) — peşin satışlar 0
        decimal Running);   // o hareketten sonraki bakiye

    // customerId -> bakiye (negatifler ve kırıntılar 0'a çekilir)
    public Dictionary<int, decimal> GetBalances(
        IEnumerable<Receipt> receipts, IEnumerable<Payment> payments)
    {
        var map = new Dictionary<int, decimal>();

        foreach (var r in receipts)
        {
            if (r.Type != ReceiptType.Veresiye) continue;
            map[r.CustomerId] = map.GetValueOrDefault(r.CustomerId) + r.Total;
        }

        foreach (var p in payments)
        {
            map[p.CustomerId] = map.GetValueOrDefault(p.CustomerId) - p.Amount;
        }

        foreach (var key in map.Keys.ToList())
        {
            if (map[key] < Epsilon) map[key] = 0m;
        }

        return map;
    }

    // Kapanmamış veresiye fişler. Tahsilatlar müşteri bazında EN ESKİ fişten
    // başlayarak (FIFO) düşülür - tasarımdaki "en eski fişten kapatılır" kuralı.
    public IReadOnlyList<OpenReceipt> GetOpenReceipts(
        IEnumerable<Receipt> receipts, IEnumerable<Payment> payments)
    {
        var pool = new Dictionary<int, decimal>();
        foreach (var p in payments)
        {
            pool[p.CustomerId] = pool.GetValueOrDefault(p.CustomerId) + p.Amount;
        }

        var open = receipts
            .Where(r => r.Type == ReceiptType.Veresiye)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.Id);

        var result = new List<OpenReceipt>();
        foreach (var r in open)
        {
            var have = pool.GetValueOrDefault(r.CustomerId);
            var used = Math.Min(have, r.Total);
            pool[r.CustomerId] = have - used;

            var remain = r.Total - used;
            if (remain > Epsilon) result.Add(new OpenReceipt(r, remain));
        }

        return result;
    }

    public IReadOnlyList<AgingBucket> GetAging(
        IEnumerable<OpenReceipt> openReceipts, DateOnly today)
    {
        (string Band, int Min, int Max)[] bands =
        {
            ("0-30", 0, 30),
            ("31-60", 31, 60),
            ("61-90", 61, 90),
            ("90+", 91, int.MaxValue),
        };

        var items = openReceipts
            .Select(o => (Age: AgeInDays(o.Receipt.Date, today), o.Remaining))
            .ToList();

        return bands.Select(b =>
        {
            var inBand = items.Where(i => i.Age >= b.Min && i.Age <= b.Max).ToList();
            return new AgingBucket(b.Band, inBand.Count, inBand.Sum(i => i.Remaining));
        }).ToList();
    }

    // Tek müşterinin tarih sıralı hesap ekstresi + yürüyen bakiye.
    public IReadOnlyList<StatementEntry> GetStatement(
        IEnumerable<Receipt> receipts, IEnumerable<Payment> payments, int customerId)
    {
        var events = new List<(DateOnly Date, int Order, Func<decimal, StatementEntry> Build)>();

        foreach (var r in receipts.Where(r => r.CustomerId == customerId))
        {
            var captured = r;
            var isVeresiye = captured.Type == ReceiptType.Veresiye;
            var detail = string.Join(", ",
                captured.Lines.Select(l => $"{l.ProductName} ×{Trim(l.Quantity)}"));
            if (!string.IsNullOrWhiteSpace(captured.Note))
                detail = detail.Length > 0 ? $"{detail} — {captured.Note}" : captured.Note!;

            events.Add((captured.Date, captured.Id, running =>
            {
                var next = isVeresiye ? running + captured.Total : running;
                return new StatementEntry(
                    captured.Date, captured.Id,
                    captured.Type.ToString().ToLowerInvariant(),
                    detail,
                    isVeresiye ? captured.Total : 0m,
                    0m,
                    next);
            }));
        }

        foreach (var p in payments.Where(p => p.CustomerId == customerId))
        {
            var captured = p;
            events.Add((captured.Date, 1_000_000 + captured.Id, running =>
            {
                var next = running - captured.Amount;
                return new StatementEntry(
                    captured.Date, null, "tahsilat",
                    string.IsNullOrWhiteSpace(captured.Note) ? $"Tahsilat · {captured.Method}" : captured.Note!,
                    0m, captured.Amount, next);
            }));
        }

        var ordered = events.OrderBy(e => e.Date).ThenBy(e => e.Order);
        var running = 0m;
        var result = new List<StatementEntry>();
        foreach (var e in ordered)
        {
            var entry = e.Build(running);
            running = entry.Running;
            result.Add(entry);
        }

        return result;
    }

    public static int AgeInDays(DateOnly date, DateOnly today)
        => Math.Max(0, today.DayNumber - date.DayNumber);

    private static string Trim(decimal value)
        => value == Math.Truncate(value)
            ? ((long)value).ToString()
            : value.ToString("0.##");
}
