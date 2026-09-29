using DemoShop.Data;
using DemoShop.Models;

namespace DemoShop.Services;

public sealed class OrderSummaryService
{
    private readonly ICustomerRepository _customers;

    public OrderSummaryService(ICustomerRepository customers) => _customers = customers;

    public async Task<IReadOnlyList<OrderSummary>> BuildSummariesAsync(IReadOnlyList<Order> orders)
    {
        var summaries = new List<OrderSummary>(orders.Count);
        foreach (var order in orders)
        {
            var customer = await _customers.GetByIdAsync(order.CustomerId);
            summaries.Add(new OrderSummary(order.Id, customer.Name, order.Lines.Count));
        }

        return summaries;
    }
}
