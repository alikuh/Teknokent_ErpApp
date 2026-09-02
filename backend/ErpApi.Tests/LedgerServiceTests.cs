using ErpApi.Models;
using ErpApi.Services;

namespace ErpApi.Tests;

public class LedgerServiceTests
{
    private readonly LedgerService _ledger = new();
    private static readonly DateOnly Today = new(2026, 9, 2);

    private static Receipt Veresiye(int id, int customerId, int daysAgo, decimal total)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            Type = ReceiptType.Veresiye,
            Date = Today.AddDays(-daysAgo),
            Total = total,
            Lines = { new ReceiptLine { ProductName = "Süt Yemi", Quantity = 1, UnitPrice = total, LineTotal = total } }
        };

    private static Receipt Cash(int id, int customerId, int daysAgo, decimal total)
        => new() { Id = id, CustomerId = customerId, Type = ReceiptType.Nakit, Date = Today.AddDays(-daysAgo), Total = total };

    private static Payment Pay(int id, int customerId, int daysAgo, decimal amount)
        => new() { Id = id, CustomerId = customerId, Date = Today.AddDays(-daysAgo), Amount = amount, Method = "Nakit" };

    [Fact]
    public void GetBalances_SumsVeresiyeMinusPayments()
    {
        var receipts = new[] { Veresiye(1, 10, 30, 1000m), Veresiye(2, 10, 10, 500m) };
        var payments = new[] { Pay(1, 10, 5, 400m) };

        var balances = _ledger.GetBalances(receipts, payments);

        Assert.Equal(1100m, balances[10]);
    }

    [Fact]
    public void GetBalances_IgnoresCashAndCardSales()
    {
        var receipts = new[] { Cash(1, 10, 3, 900m), Veresiye(2, 10, 2, 100m) };

        var balances = _ledger.GetBalances(receipts, Array.Empty<Payment>());

        Assert.Equal(100m, balances[10]);
    }

    [Fact]
    public void GetBalances_ClampsOverpaymentToZero()
    {
        var receipts = new[] { Veresiye(1, 10, 10, 300m) };
        var payments = new[] { Pay(1, 10, 1, 500m) };

        var balances = _ledger.GetBalances(receipts, payments);

        Assert.Equal(0m, balances[10]);
    }

    [Fact]
    public void GetOpenReceipts_AppliesPaymentsOldestFirst()
    {
        var receipts = new[]
        {
            Veresiye(1, 10, 40, 1000m),
            Veresiye(2, 10, 10, 800m)
        };
        var payments = new[] { Pay(1, 10, 5, 1200m) };

        var open = _ledger.GetOpenReceipts(receipts, payments);

        // İlk fiş tamamen kapanır (elenir), ikinciden 200 kalır.
        var only = Assert.Single(open);
        Assert.Equal(2, only.Receipt.Id);
        Assert.Equal(600m, only.Remaining);
    }

    [Fact]
    public void GetOpenReceipts_ExcludesFullyPaidAndNonVeresiye()
    {
        var receipts = new[]
        {
            Veresiye(1, 10, 20, 500m),
            Cash(2, 10, 15, 999m)
        };
        var payments = new[] { Pay(1, 10, 1, 500m) };

        Assert.Empty(_ledger.GetOpenReceipts(receipts, payments));
    }

    [Fact]
    public void GetOpenReceipts_PaymentsAreScopedPerCustomer()
    {
        var receipts = new[] { Veresiye(1, 10, 10, 300m), Veresiye(2, 20, 10, 300m) };
        var payments = new[] { Pay(1, 10, 1, 300m) }; // sadece müşteri 10'u kapatır

        var open = _ledger.GetOpenReceipts(receipts, payments);

        var only = Assert.Single(open);
        Assert.Equal(20, only.Receipt.CustomerId);
    }

    [Fact]
    public void GetAging_BucketsByReceiptAge()
    {
        var receipts = new[]
        {
            Veresiye(1, 10, 5, 100m),    // 0-30
            Veresiye(2, 11, 45, 200m),   // 31-60
            Veresiye(3, 12, 75, 300m),   // 61-90
            Veresiye(4, 13, 120, 400m)   // 90+
        };

        var open = _ledger.GetOpenReceipts(receipts, Array.Empty<Payment>());
        var aging = _ledger.GetAging(open, Today);

        Assert.Equal(100m, aging.Single(a => a.Band == "0-30").Amount);
        Assert.Equal(200m, aging.Single(a => a.Band == "31-60").Amount);
        Assert.Equal(300m, aging.Single(a => a.Band == "61-90").Amount);
        Assert.Equal(400m, aging.Single(a => a.Band == "90+").Amount);
        Assert.Equal(1, aging.Single(a => a.Band == "90+").Count);
    }

    [Fact]
    public void GetAging_BoundaryDayGoesToLowerBucket()
    {
        var receipts = new[] { Veresiye(1, 10, 30, 100m), Veresiye(2, 11, 31, 100m) };

        var open = _ledger.GetOpenReceipts(receipts, Array.Empty<Payment>());
        var aging = _ledger.GetAging(open, Today);

        Assert.Equal(100m, aging.Single(a => a.Band == "0-30").Amount);
        Assert.Equal(100m, aging.Single(a => a.Band == "31-60").Amount);
    }

    [Fact]
    public void GetStatement_RunningBalanceTracksDebitAndCredit()
    {
        var receipts = new[] { Veresiye(1, 10, 20, 1000m), Cash(2, 10, 15, 500m), Veresiye(3, 10, 10, 200m) };
        var payments = new[] { Pay(1, 10, 5, 400m) };

        var statement = _ledger.GetStatement(receipts, payments, 10);

        Assert.Equal(4, statement.Count);
        Assert.Equal(1000m, statement[0].Running);  // veresiye +1000
        Assert.Equal(1000m, statement[1].Running);  // nakit satış bakiyeyi değiştirmez
        Assert.Equal(0m, statement[1].Debit);
        Assert.Equal(1200m, statement[2].Running);  // veresiye +200
        Assert.Equal(800m, statement[3].Running);   // tahsilat -400
        Assert.Equal(400m, statement[3].Credit);
    }

    [Fact]
    public void GetStatement_OnlyIncludesRequestedCustomer()
    {
        var receipts = new[] { Veresiye(1, 10, 5, 100m), Veresiye(2, 99, 5, 900m) };

        var statement = _ledger.GetStatement(receipts, Array.Empty<Payment>(), 10);

        Assert.Single(statement);
        Assert.Equal(100m, statement[0].Debit);
    }

    [Fact]
    public void GetBalances_EmptyLedgerIsEmpty()
    {
        Assert.Empty(_ledger.GetBalances(Array.Empty<Receipt>(), Array.Empty<Payment>()));
    }
}
