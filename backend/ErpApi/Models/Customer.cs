namespace ErpApi.Models;

public class Customer
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Köy / mahalle - raporlarda alacak bu alana göre gruplanır.
    public string? Village { get; set; }
    public string? Phone { get; set; }
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
