using DemoShop.Models;

namespace DemoShop.Data;

public sealed class InMemoryProductRepository : IProductRepository
{
    public Task<Product> GetByIdAsync(int id) => Task.FromResult(new Product(id, $"Product {id}", 9.99m + id));
}
