namespace DemoShop.Services;

public sealed class InventoryEventHub
{
    public event EventHandler<int>? StockLow;

    public void RaiseStockLow(int productId) => StockLow?.Invoke(this, productId);
}

public sealed class InventoryAlertHandler : IDisposable
{
    private readonly InventoryEventHub _hub;
    private int _alerts;

    public InventoryAlertHandler(InventoryEventHub hub)
    {
        _hub = hub;
        _hub.StockLow += OnStockLow;
    }

    public int Alerts => Volatile.Read(ref _alerts);

    public void Dispose() => _hub.StockLow -= OnStockLow;

    private void OnStockLow(object? sender, int productId) => Interlocked.Increment(ref _alerts);
}
