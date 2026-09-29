using DemoShop.Models;

namespace DemoShop.Services;

public sealed class OrderEventHub
{
    public event EventHandler<Order>? OrderPlaced;

    public void Publish(Order order) => OrderPlaced?.Invoke(this, order);
}

public sealed class OrderAuditHandler
{
    private int _auditedOrders;

    public OrderAuditHandler(OrderEventHub hub)
    {
        hub.OrderPlaced += OnOrderPlaced;
    }

    public int AuditedOrders => Volatile.Read(ref _auditedOrders);

    private void OnOrderPlaced(object? sender, Order order) => Interlocked.Increment(ref _auditedOrders);
}
