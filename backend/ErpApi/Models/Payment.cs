namespace ErpApi.Models;

public class Payment
{
    public int Id { get; set; }
    public int UserId { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }

    // "Nakit" / "Havale" / "Kart" - serbest metin.
    public string Method { get; set; } = "Nakit";
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
