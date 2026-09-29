using DemoShop.Data;
using DemoShop.Models;
using Microsoft.Extensions.Caching.Memory;

namespace DemoShop.Services;

public sealed class ProductCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1_000 });
    private readonly IProductRepository _products;

    public ProductCache(IProductRepository products) => _products = products;

    public async Task<Product> GetAsync(int id)
    {
        if (_cache.TryGetValue(id, out Product? cached) && cached is not null)
        {
            return cached;
        }

        var product = await _products.GetByIdAsync(id);
        _cache.Set(id, product, new MemoryCacheEntryOptions { Size = 1 });
        return product;
    }

    public void Dispose() => _cache.Dispose();
}
