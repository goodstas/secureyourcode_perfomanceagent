namespace SecureYourCode.PerformanceAnalyzer.Tests;

public class UnboundedTaskFanOutAnalyzerTests
{
    private const string Sender = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;

        public interface ISender
        {
            Task SendAsync(string recipient);
            Task SendBatchAsync(string[] recipients);
            Task<IReadOnlyList<string>> LoadRecipientsAsync();
        }
        """;

    [Fact]
    public Task WhenAllOverParameter_Reports() => Verify<UnboundedTaskFanOutAnalyzer>.AnalyzerAsync(Sender + """

        public sealed class NotificationService(ISender sender)
        {
            public Task NotifyAllAsync(IReadOnlyList<string> recipients)
            {
                return {|PERF003:Task.WhenAll(recipients.Select(sender.SendAsync))|};
            }

            public Task NotifyWithLambdaAsync(IReadOnlyList<string> recipients) =>
                {|PERF003:Task.WhenAll(recipients.Select(r => sender.SendAsync(r)).ToList())|};
        }
        """);

    [Fact]
    public Task WhenAllOverFieldAndQueryResult_Reports() => Verify<UnboundedTaskFanOutAnalyzer>.AnalyzerAsync(Sender + """

        public sealed class Broadcaster(ISender sender)
        {
            private readonly List<string> _subscribers = new();

            public Task ToSubscribersAsync() => {|PERF003:Task.WhenAll(_subscribers.Select(sender.SendAsync))|};

            public async Task ToLoadedRecipientsAsync()
            {
                var recipients = await sender.LoadRecipientsAsync();
                await {|PERF003:Task.WhenAll(recipients.Where(r => r.Length > 0).Select(sender.SendAsync))|};
            }
        }
        """);

    [Fact]
    public Task WhenAllOverChunkedSelect_StillInputSized_Reports() => Verify<UnboundedTaskFanOutAnalyzer>.AnalyzerAsync(Sender + """

        public sealed class ChunkedService(ISender sender)
        {
            public Task NotifyAllAsync(IReadOnlyList<string> recipients) =>
                {|PERF003:Task.WhenAll(recipients.Chunk(20).Select(sender.SendBatchAsync))|};
        }
        """);

    [Fact]
    public Task SequentialBoundedBatches_NoDiagnostic() => Verify<UnboundedTaskFanOutAnalyzer>.AnalyzerAsync(Sender + """

        public sealed class BatchedNotificationService(ISender sender)
        {
            public async Task NotifyAllInBatchesAsync(IReadOnlyList<string> recipients)
            {
                foreach (var batch in recipients.Chunk(20))
                {
                    await Task.WhenAll(batch.Select(sender.SendAsync));
                }
            }
        }
        """);

    [Fact]
    public Task CollectionLiteralsAndFixedArrays_NoDiagnostic() => Verify<UnboundedTaskFanOutAnalyzer>.AnalyzerAsync(Sender + """

        public sealed class FixedService(ISender sender)
        {
            public async Task RunAsync()
            {
                await Task.WhenAll(new[] { "ops@example.test", "oncall@example.test" }.Select(sender.SendAsync));

                string[] admins = ["a@example.test", "b@example.test"];
                await Task.WhenAll(admins.Select(sender.SendAsync));

                await Task.WhenAll(Enumerable.Range(0, 3).Select(i => sender.SendAsync(i.ToString())));
            }
        }
        """);
}
