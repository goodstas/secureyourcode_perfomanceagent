namespace DemoShop.Notifications;

public interface ISender
{
    Task SendAsync(string recipient);
}
