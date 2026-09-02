namespace ErpApi.Models;

public enum ReceiptType
{
    // Sadece Veresiye fişler cari bakiyeye borç yazar; Nakit/Kart peşindir.
    Veresiye,
    Nakit,
    Kart
}

public class Receipt
{
    public int Id { get; set; }
    public int UserId { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // İş günü (saat bilgisi taşımaz); yaşlandırma bu tarihe göre hesaplanır.
    public DateOnly Date { get; set; }

    public ReceiptType Type { get; set; } = ReceiptType.Veresiye;
    public string? Note { get; set; }

    // Satır toplamlarının denormalize kopyası - liste/rapor sorgularında
    // her seferinde satırları toplamamak için.
    public decimal Total { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ReceiptLine> Lines { get; set; } = new();
}

public class ReceiptLine
{
    public int Id { get; set; }

    public int ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }

    // Ürün sonradan silinebilir; o yüzden nullable ve ad/fiyat fişte
    // "snapshot" tutulur - geçmiş fişler üründen bağımsız okunabilsin.
    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}
