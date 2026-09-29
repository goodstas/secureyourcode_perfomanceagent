// RepositoryCallAmplification / OrderCustomerLookup (plan §4.6).
// Seam: OrderSummaryService.BuildSummariesAsync. Measures repository calls for n orders with distinct customers.
// Prints exactly one JSON line: { "n": <n>, "metric": "repository calls", "value": <calls> }.
using System.Text.Json;
using DemoShop.Data;
using DemoShop.Models;
using DemoShop.Services;

var n = args.Length == 2 && args[0] == "--n" && int.TryParse(args[1], out var parsed) && parsed > 0
    ? parsed
    : throw new ArgumentException("usage: --n <positive integer>");

var repository = new CountingCustomerRepository();
var service = new OrderSummaryService(repository);
var orders = Enumerable.Range(1, n)
    .Select(i => new Order(i, CustomerId: i, [new OrderLine(ProductId: i, Quantity: 1)]))
    .ToList();

await service.BuildSummariesAsync(orders);

Console.WriteLine(JsonSerializer.Serialize(new { n, metric = "repository calls", value = repository.CallCount }));
