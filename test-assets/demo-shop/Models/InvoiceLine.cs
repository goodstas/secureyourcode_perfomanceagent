namespace DemoShop.Models;

public sealed record InvoiceLine(int OrderId, string ProductName, decimal Total);
