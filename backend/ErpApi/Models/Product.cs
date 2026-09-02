namespace ErpApi.Models;

public class Product
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;

    // "çuval" / "kg" / "balya" gibi serbest metin satış birimi.
    public string Unit { get; set; } = "adet";

    public decimal Price { get; set; }

    // Kesirli miktar satılabildiği için (örn. 12.5 kg) stok da ondalık.
    public decimal Stock { get; set; }

    // Bu değerin altındaki stok "kritik" sayılır. Boşsa
    // appsettings'teki genel eşik (Erp:CriticalStockThreshold) kullanılır.
    public decimal? CriticalStockThreshold { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
