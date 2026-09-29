using DemoShop.Models;

namespace DemoShop.Data;

public interface IProductRepository
{
    Task<Product> GetByIdAsync(int id);
}
