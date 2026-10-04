using SecureYourCode.Agent.Infrastructure;

namespace SecureYourCode.Agent.Graph;

public enum GraphRefreshStatus
{
    Current,
    Failed,
}

public sealed record GraphRefreshResult(GraphRefreshStatus Status, string? Fingerprint, PublishedGraph? Graph, string? Reason)
{
    public static GraphRefreshResult Current(PublishedGraph graph) => new(GraphRefreshStatus.Current, graph.Fingerprint, graph, null);

    public static GraphRefreshResult Failed(string? fingerprint, string reason) => new(GraphRefreshStatus.Failed, fingerprint, null, reason);
}

/// <summary>A published graph: &lt;StateRoot&gt;/graphs/&lt;fingerprint&gt;-&lt;graphifyVersion&gt;/&lt;graphifyOutputRelativePath&gt;.</summary>
public sealed record PublishedGraph(string Fingerprint, string GraphifyVersion, string Directory, string GraphJsonPath);

/// <summary>
/// Graph refresh (plan §4.1, §4.2). A DI singleton: its gate makes background, startup and /analyze refreshes never overlap.
/// A graph is published only when the fingerprint is unchanged across extraction, and a published folder is never modified.
/// </summary>
public sealed class GraphifyUpdater(StatePaths paths, IGraphExtractor extractor, ILogger<GraphifyUpdater> logger)
{
    public const string SourceChangedReason = "source_changed_during_graph_build";
    public const string TimeoutReason = "graph_refresh_timeout";
    public const string FailedReason = "graph_refresh_failed";
    private const string CurrentPointer = "current.txt";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The graph-refresh limit (plan §2: 5 min). It covers waiting for a refresh already in progress and this refresh,
    /// so /analyze waits at most this long for its graph (plan §4.8 step 2).
    /// </summary>
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Refreshes the graph. Only the caller's cancellation throws; the time limit and every other problem (Graphify not
    /// installed, a failed extraction) end as <see cref="GraphRefreshStatus.Failed"/> with a reason, so neither /analyze
    /// nor the background worker is ever stopped by a graph problem.
    /// </summary>
    public async Task<GraphRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshTimeout);
        try
        {
            await _gate.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Graph refresh not started: another refresh was still running after {Timeout}", RefreshTimeout);
            return GraphRefreshResult.Failed(null, $"{TimeoutReason}: another graph refresh was still running after {Minutes(RefreshTimeout)}");
        }

        try
        {
            return await RefreshCoreAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The time limit, not the caller: the extraction's process tree has been stopped by ProcessRunner.
            logger.LogWarning("Graph refresh stopped after {Timeout}", RefreshTimeout);
            return GraphRefreshResult.Failed(null, $"{TimeoutReason}: the graph refresh did not finish within {Minutes(RefreshTimeout)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Before extraction: the Graphify version or the fingerprint could not be read (e.g. Graphify not installed).
            logger.LogWarning(exception, "Graph refresh failed");
            return GraphRefreshResult.Failed(null, $"{FailedReason}: {exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The published graph for exactly this fingerprint, if any (graph status "current" in /analyze).</summary>
    public async Task<PublishedGraph?> TryGetPublishedAsync(string fingerprint, CancellationToken cancellationToken)
    {
        var version = await extractor.GetVersionAsync(cancellationToken);
        return Published(fingerprint, version);
    }

    /// <summary>The graph named by current.txt, if any (a "stale" graph when its fingerprint differs from the run's).</summary>
    public PublishedGraph? ReadCurrent()
    {
        var pointer = Path.Combine(paths.GraphsDirectory, CurrentPointer);
        if (!File.Exists(pointer))
        {
            return null;
        }

        var name = File.ReadAllText(pointer).Trim();
        var separator = name.IndexOf('-');
        return separator <= 0 ? null : Published(name[..separator], name[(separator + 1)..]);
    }

    private async Task<GraphRefreshResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var version = await extractor.GetVersionAsync(cancellationToken);
        var before = await RepoFingerprint.ComputeAsync(paths.RepoPath, cancellationToken);
        if (Published(before, version) is { } existing)
        {
            UpdateCurrentPointer(existing);
            return GraphRefreshResult.Current(existing);
        }

        Directory.CreateDirectory(paths.GraphsDirectory);
        var temp = Path.Combine(paths.GraphsDirectory, $"tmp-{Guid.NewGuid():N}");
        try
        {
            await extractor.ExtractAsync(paths.RepoPath, temp, cancellationToken);
            var after = await RepoFingerprint.ComputeAsync(paths.RepoPath, cancellationToken);
            if (after != before)
            {
                logger.LogWarning("Graph not published: {Reason}", SourceChangedReason);
                return GraphRefreshResult.Failed(before, SourceChangedReason);
            }

            var graphFile = GraphFile(temp);
            if (!GraphStructure.IsValidGraphFile(graphFile))
            {
                logger.LogWarning("Graph not published: {GraphFile} is missing, empty, not valid JSON or fails graphStructurePredicate", graphFile);
                return GraphRefreshResult.Failed(before, "invalid_graph_output");
            }

            var target = Path.Combine(paths.GraphsDirectory, FolderName(before, version));
            Directory.Move(temp, target);
            var published = Published(before, version)!;
            UpdateCurrentPointer(published);
            logger.LogInformation("Published graph {Folder}", Path.GetFileName(target));
            return GraphRefreshResult.Current(published);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Graph extraction failed");
            return GraphRefreshResult.Failed(before, $"extraction_failed: {exception.Message}");
        }
        finally
        {
            DeleteDirectory(temp);
        }
    }

    private PublishedGraph? Published(string fingerprint, string version)
    {
        var directory = Path.Combine(paths.GraphsDirectory, FolderName(fingerprint, version));
        var graphJson = GraphFile(directory);
        return File.Exists(graphJson) ? new PublishedGraph(fingerprint, version, directory, graphJson) : null;
    }

    /// <summary>Always locate the graph via graphifyOutputRelativePath (plan §4.1), with native separators.</summary>
    private static string GraphFile(string outputFolder) =>
        Path.GetFullPath(Path.Combine(outputFolder, GraphStructure.GraphifyOutputRelativePath));

    private void UpdateCurrentPointer(PublishedGraph graph)
    {
        var pointer = Path.Combine(paths.GraphsDirectory, CurrentPointer);
        var temp = $"{pointer}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, FolderName(graph.Fingerprint, graph.GraphifyVersion));
        File.Move(temp, pointer, overwrite: true);
    }

    private static string FolderName(string fingerprint, string version) => $"{fingerprint}-{version}";

    private static string Minutes(TimeSpan span) => span.TotalMinutes >= 1
        ? FormattableString.Invariant($"{span.TotalMinutes:0.#} min")
        : FormattableString.Invariant($"{span.TotalSeconds:0.#} s");

    /// <summary>Best effort: a leftover tmp-* folder is never read as a graph, so a locked file must not fail the refresh.</summary>
    private void DeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete the temporary graph folder {Folder}", path);
        }
    }
}
