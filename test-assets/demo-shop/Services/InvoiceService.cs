using DemoShop.Data;
using DemoShop.Models;

namespace DemoShop.Services;

public sealed class InvoiceService
{
    private readonly IProductRepository _products;

    public InvoiceService(IProductRepository products) => _products = products;

    public async Task<IReadOnlyList<InvoiceLine>> BuildInvoiceLinesAsync(IReadOnlyList<Order> orders)
    {
        var lines = new List<InvoiceLine>();
        foreach (var order in orders)
        {
            foreach (var line in order.Lines)
            {
                var product = await _products.GetByIdAsync(line.ProductId);
                lines.Add(new InvoiceLine(order.Id, product.Name, product.Price * line.Quantity));
            }
        }

        return lines;
    }
}
