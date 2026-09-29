using DemoShop.Notifications;

namespace DemoShop.Services;

public sealed class NotificationService
{
    private readonly ISender _sender;

    public NotificationService(ISender sender) => _sender = sender;

    public Task NotifyAllAsync(IReadOnlyList<string> recipients)
    {
        return Task.WhenAll(recipients.Select(_sender.SendAsync));
    }
}
