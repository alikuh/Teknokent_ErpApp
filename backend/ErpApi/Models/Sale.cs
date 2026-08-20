namespace ErpApi.Models;

public class Sale
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public DateTime SoldAt { get; set; } = DateTime.UtcNow;
}