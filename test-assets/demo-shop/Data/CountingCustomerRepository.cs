using DemoShop.Models;

namespace DemoShop.Data;

public sealed class CountingCustomerRepository : ICustomerRepository
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<Customer> GetByIdAsync(int id)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(CreateCustomer(id));
    }

    public Task<IReadOnlyDictionary<int, Customer>> GetByIdsAsync(IReadOnlyCollection<int> ids)
    {
        Interlocked.Increment(ref _callCount);
        IReadOnlyDictionary<int, Customer> customers = ids.Distinct().ToDictionary(id => id, CreateCustomer);
        return Task.FromResult(customers);
    }

    private static Customer CreateCustomer(int id) => new(id, $"Customer {id}");
}
