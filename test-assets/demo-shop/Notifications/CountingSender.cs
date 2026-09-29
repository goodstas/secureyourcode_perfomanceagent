namespace DemoShop.Notifications;

public sealed class CountingSender : ISender
{
    private int _startedCount;

    public int StartedCount => Volatile.Read(ref _startedCount);

    public async Task SendAsync(string recipient)
    {
        Interlocked.Increment(ref _startedCount);
        await Task.Delay(5);
    }
}
