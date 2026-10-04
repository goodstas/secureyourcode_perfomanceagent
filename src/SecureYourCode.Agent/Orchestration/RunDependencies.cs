using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Reporting;
using SecureYourCode.Agent.StaticAnalysis;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>Whole-run and per-session limits (plan §2).</summary>
public sealed record OrchestrationTimeouts(TimeSpan Run, TimeSpan Session)
{
    public static readonly OrchestrationTimeouts Default = new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10));
}

public interface IRepositorySnapshot
{
    Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken);

    Task<RepoState> ReadStateAsync(CancellationToken cancellationToken);
}

public sealed class RepositorySnapshot(StatePaths paths) : IRepositorySnapshot
{
    public Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken) => RepoFingerprint.ComputeAsync(paths.RepoPath, cancellationToken);

    public Task<RepoState> ReadStateAsync(CancellationToken cancellationToken) => RepoState.ReadAsync(paths.RepoPath, cancellationToken);
}

/// <summary>The graph for one run: its status, the graph the reviewers use (if any), and why it is not current.</summary>
public sealed record GraphSelection(string Status, PublishedGraph? Graph, string? Problem = null);

public interface IGraphSelector
{
    /// <summary>
    /// Graph status for the run (plan §4.8 step 2) and the graph the reviewers use, if any. Only the caller's
    /// cancellation throws: a graph problem ends as "stale" or "none" with <see cref="GraphSelection.Problem"/> set.
    /// </summary>
    Task<GraphSelection> SelectAsync(string analysisFingerprint, CancellationToken cancellationToken);
}

public sealed class GraphSelector(GraphifyUpdater updater, ILogger<GraphSelector> logger) : IGraphSelector
{
    public async Task<GraphSelection> SelectAsync(string analysisFingerprint, CancellationToken cancellationToken)
    {
        try
        {
            if (await updater.TryGetPublishedAsync(analysisFingerprint, cancellationToken) is { } published)
            {
                return new GraphSelection(GraphStatuses.Current, published);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The installed Graphify version could not be read: Graphify cannot serve any graph, current or stale (plan §4.1).
            logger.LogWarning(exception, "Graphify unavailable");
            return new GraphSelection(GraphStatuses.None, null, $"the installed Graphify version could not be read ({exception.Message})");
        }

        var refreshed = await updater.RefreshAsync(cancellationToken);
        if (refreshed.Status == GraphRefreshStatus.Current && refreshed.Graph!.Fingerprint == analysisFingerprint)
        {
            return new GraphSelection(GraphStatuses.Current, refreshed.Graph);
        }

        var problem = refreshed.Status == GraphRefreshStatus.Failed
            ? $"graph refresh failed ({refreshed.Reason})"
            : "the source changed before the graph refresh, so the newest graph is for another fingerprint";
        PublishedGraph? older;
        try
        {
            older = updater.ReadCurrent();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the current graph pointer");
            older = null;
        }

        return older is null
            ? new GraphSelection(GraphStatuses.None, null, problem)
            : new GraphSelection(GraphStatuses.Stale, older, problem);
    }
}

public interface IStaticAnalysis
{
    Task<StaticAnalysisResult> RunAsync(string analysisFingerprint, string runDirectory, CancellationToken cancellationToken);
}

/// <summary>Publishes report.json / report.html (plan §4.7); publication never changes the run status.</summary>
public interface IReportPublisher
{
    Task PublishAsync(Report report, string runDirectory, CancellationToken cancellationToken);
}
