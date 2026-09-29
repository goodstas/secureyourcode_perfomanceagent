// TaskFanOut / NotificationRecipients (plan §4.6).
// Seam: NotificationService.NotifyAllAsync. Measures the tasks started by one call with n recipients, read as soon as the
// call returns (before awaiting completion), so it counts sends that are in flight at the same time.
// Prints exactly one JSON line: { "n": <n>, "metric": "tasks started", "value": <started> }.
using System.Text.Json;
using DemoShop.Notifications;
using DemoShop.Services;

var n = args.Length == 2 && args[0] == "--n" && int.TryParse(args[1], out var parsed) && parsed > 0
    ? parsed
    : throw new ArgumentException("usage: --n <positive integer>");

var sender = new CountingSender();
var service = new NotificationService(sender);
var recipients = Enumerable.Range(1, n).Select(i => $"recipient-{i}@example.test").ToList();

var notification = service.NotifyAllAsync(recipients);
var startedAtOnce = sender.StartedCount;
await notification;

Console.WriteLine(JsonSerializer.Serialize(new { n, metric = "tasks started", value = startedAtOnce }));
