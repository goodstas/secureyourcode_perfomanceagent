using DemoShop.Data;
using DemoShop.Models;
using DemoShop.Notifications;
using DemoShop.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ICustomerRepository, CountingCustomerRepository>();
builder.Services.AddSingleton<IProductRepository, InMemoryProductRepository>();
builder.Services.AddSingleton<ISender, CountingSender>();
builder.Services.AddScoped<OrderSummaryService>();
builder.Services.AddScoped<BatchedOrderService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<BatchedNotificationService>();
builder.Services.AddSingleton<ProductCache>();
builder.Services.AddSingleton<OrderEventHub>();
builder.Services.AddScoped<OrderAuditHandler>();
builder.Services.AddSingleton<InventoryEventHub>();
builder.Services.AddScoped<InventoryAlertHandler>();

var app = builder.Build();

app.MapPost("/orders/summaries", (IReadOnlyList<Order> orders, OrderSummaryService service) =>
    service.BuildSummariesAsync(orders));

app.MapPost("/orders/summaries/batched", (IReadOnlyList<Order> orders, BatchedOrderService service) =>
    service.BuildSummariesAsync(orders));

app.MapPost("/invoices", (IReadOnlyList<Order> orders, InvoiceService service) =>
    service.BuildInvoiceLinesAsync(orders));

app.MapPost("/notifications", (IReadOnlyList<string> recipients, NotificationService service) =>
    service.NotifyAllAsync(recipients));

app.MapPost("/notifications/batched", (IReadOnlyList<string> recipients, BatchedNotificationService service) =>
    service.NotifyAllInBatchesAsync(recipients));

app.MapGet("/reports/{key}", (string key) => ReportCache.GetOrAdd(key));

app.MapGet("/products/{id:int}", (int id, ProductCache cache) => cache.GetAsync(id));

app.MapPost("/orders/placed", (Order order, OrderEventHub hub, OrderAuditHandler audit) =>
{
    hub.Publish(order);
    return Results.Accepted();
});

app.MapPost("/inventory/{productId:int}/low", (int productId, InventoryEventHub hub, InventoryAlertHandler alerts) =>
{
    hub.RaiseStockLow(productId);
    return Results.Accepted();
});

app.Run();
