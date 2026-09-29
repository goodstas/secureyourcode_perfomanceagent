using DemoShop.Data;
using DemoShop.Models;

namespace DemoShop.Services;

public sealed class BatchedOrderService
{
    private readonly ICustomerRepository _customers;

    public BatchedOrderService(ICustomerRepository customers) => _customers = customers;

    public async Task<IReadOnlyList<OrderSummary>> BuildSummariesAsync(IReadOnlyList<Order> orders)
    {
        var customerIds = orders.Select(order => order.CustomerId).Distinct().ToArray();
        var customers = await _customers.GetByIdsAsync(customerIds);

        var summaries = new List<OrderSummary>(orders.Count);
        foreach (var order in orders)
        {
            var customer = customers[order.CustomerId];
            summaries.Add(new OrderSummary(order.Id, customer.Name, order.Lines.Count));
        }

        return summaries;
    }
}
