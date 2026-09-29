using System.Threading.Channels;

namespace SecureYourCode.Agent.Graph;

/// <summary>
/// The graph-refresh queue (plan §4.2): a DI-singleton channel of capacity 1 that drops writes while a refresh is
/// already pending, so a burst of commits causes at most one extra refresh.
/// </summary>
public sealed class GraphRefreshQueue
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _channel.Writer.TryWrite(true);

    public IAsyncEnumerable<bool> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Reads the refresh queue and runs GraphifyUpdater.RefreshAsync in the background.</summary>
public sealed class GraphRefreshWorker(GraphRefreshQueue queue, GraphifyUpdater updater, ILogger<GraphRefreshWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var result = await updater.RefreshAsync(stoppingToken);
                    logger.LogInformation("Background graph refresh: {Status} {Reason}", result.Status, result.Reason ?? "");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Background graph refresh failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
    }
}
