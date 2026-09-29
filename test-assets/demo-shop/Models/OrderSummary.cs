namespace DemoShop.Models;

public sealed record OrderSummary(int OrderId, string CustomerName, int LineCount);
