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

public interface IGraphSelector
{
    /// <summary>Graph status for the run (plan §4.8 step 2) and the graph the reviewers use, if any.</summary>
    Task<(string Status, PublishedGraph? Graph)> SelectAsync(string analysisFingerprint, CancellationToken cancellationToken);
}

public sealed class GraphSelector(GraphifyUpdater updater) : IGraphSelector
{
    public async Task<(string Status, PublishedGraph? Graph)> SelectAsync(string analysisFingerprint, CancellationToken cancellationToken)
    {
        if (await updater.TryGetPublishedAsync(analysisFingerprint, cancellationToken) is { } published)
        {
            return (GraphStatuses.Current, published);
        }

        var refreshed = await updater.RefreshAsync(cancellationToken);
        if (refreshed.Status == GraphRefreshStatus.Current && refreshed.Graph!.Fingerprint == analysisFingerprint)
        {
            return (GraphStatuses.Current, refreshed.Graph);
        }

        return updater.ReadCurrent() is { } older ? (GraphStatuses.Stale, older) : (GraphStatuses.None, null);
    }
}

public interface IStaticAnalysis
{
    Task<StaticAnalysisResult> RunAsync(string analysisFingerprint, string runDirectory, CancellationToken cancellationToken);
}

/// <summary>Publishes report.json / report.html (plan §4.7). Implemented in H6; publication never changes the run status.</summary>
public interface IReportPublisher
{
    Task PublishAsync(Report report, string runDirectory, CancellationToken cancellationToken);
}

public sealed class PendingReportPublisher(ILogger<PendingReportPublisher> logger) : IReportPublisher
{
    public Task PublishAsync(Report report, string runDirectory, CancellationToken cancellationToken)
    {
        logger.LogInformation("Run {RunId} finished with status {Status}; report files are published from H6 on", report.Run.RunId, report.Run.Status);
        return Task.CompletedTask;
    }
}
