using Microsoft.EntityFrameworkCore;
using ErpApi.Models;

namespace ErpApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products { get; set; }
    public DbSet<Customer> Customers { get; set; }
    public DbSet<Receipt> Receipts { get; set; }
    public DbSet<ReceiptLine> ReceiptLines { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<User> Users { get; set; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Tüm parasal/miktar alanları için tek noktadan ondalık hassasiyet.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Enum'u okunur biçimde ("Veresiye") sakla, sayı olarak değil.
        modelBuilder.Entity<Receipt>()
            .Property(r => r.Type)
            .HasConversion<string>()
            .HasMaxLength(20);

        modelBuilder.Entity<Receipt>()
            .HasMany(r => r.Lines)
            .WithOne(l => l.Receipt!)
            .HasForeignKey(l => l.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ürün silinince fiş satırı kalır, sadece bağ kopar (ad/fiyat snapshot'ta).
        modelBuilder.Entity<ReceiptLine>()
            .HasOne(l => l.Product)
            .WithMany()
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.SetNull);

        // Geçmişi (fiş/tahsilat) olan müşteri silinemesin.
        modelBuilder.Entity<Receipt>()
            .HasOne(r => r.Customer)
            .WithMany()
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payment>()
            .HasOne(p => p.Customer)
            .WithMany()
            .HasForeignKey(p => p.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Kullanıcı bazlı sorgular ve müşteri araması için indeksler.
        modelBuilder.Entity<Customer>().HasIndex(c => c.UserId);
        modelBuilder.Entity<Product>().HasIndex(p => p.UserId);
        modelBuilder.Entity<Receipt>().HasIndex(r => new { r.UserId, r.Date });
        modelBuilder.Entity<Payment>().HasIndex(p => new { p.UserId, p.Date });
    }
}
