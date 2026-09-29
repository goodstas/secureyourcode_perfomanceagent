namespace DemoShop.Models;

public sealed record Order(int Id, int CustomerId, IReadOnlyList<OrderLine> Lines);

public sealed record OrderLine(int ProductId, int Quantity);
