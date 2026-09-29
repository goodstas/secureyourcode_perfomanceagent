using DemoShop.Models;

namespace DemoShop.Data;

public interface ICustomerRepository
{
    Task<Customer> GetByIdAsync(int id);

    Task<IReadOnlyDictionary<int, Customer>> GetByIdsAsync(IReadOnlyCollection<int> ids);
}
