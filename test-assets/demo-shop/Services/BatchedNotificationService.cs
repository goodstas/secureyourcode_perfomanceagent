using DemoShop.Notifications;

namespace DemoShop.Services;

public sealed class BatchedNotificationService
{
    private readonly ISender _sender;

    public BatchedNotificationService(ISender sender) => _sender = sender;

    public async Task NotifyAllInBatchesAsync(IReadOnlyList<string> recipients)
    {
        foreach (var batch in recipients.Chunk(20))
        {
            await Task.WhenAll(batch.Select(_sender.SendAsync));
        }
    }
}
