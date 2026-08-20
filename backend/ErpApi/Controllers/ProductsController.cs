using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErpApi.Data;
using ErpApi.Models;
using ErpApi.Services;
using StackExchange.Redis;

namespace ErpApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConnectionMultiplexer _redis;

    public ProductsController(AppDbContext context, IConnectionMultiplexer redis)
    {
        _context = context;
        _redis = redis;
    }

    // GET: api/products
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Product>>> GetProducts([FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        return await _context.Products.Where(p => p.UserId == userId).ToListAsync();
    }

    // GET: api/products/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetProduct(int id, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var product = await _context.Products.FindAsync(id);

        if (product == null || product.UserId != userId)
        {
            return NotFound();
        }

        return product;
    }

    // POST: api/products
    [HttpPost]
    public async Task<ActionResult<Product>> CreateProduct(Product product, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        product.UserId = userId.Value;
        product.CreatedAt = DateTime.UtcNow;
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
    }

    // PUT: api/products/5
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProduct(int id, Product product, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var existingProduct = await _context.Products.FindAsync(id);
        if (existingProduct == null || existingProduct.UserId != userId)
        {
            return NotFound();
        }

        existingProduct.Name = product.Name;
        existingProduct.Description = product.Description;
        existingProduct.Price = product.Price;
        existingProduct.StockQuantity = product.StockQuantity;
        existingProduct.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return NoContent();
    }

    // DELETE: api/products/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProduct(int id, [FromHeader(Name = "Authorization")] string? token)
    {
        var userId = await AuthHelper.GetUserIdAsync(_redis, token);
        if (userId == null) return Unauthorized("Giriş yapmalısınız.");

        var product = await _context.Products.FindAsync(id);
        if (product == null || product.UserId != userId)
        {
            return NotFound();
        }

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}