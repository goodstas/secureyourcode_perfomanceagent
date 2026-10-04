using Microsoft.Extensions.Logging.Abstractions;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.Reporting;

namespace SecureYourCode.Agent.Tests;

public class FingerprintAndGraphTests
{
    private const string ValidGraph = """{"nodes":[{"id":"a","label":"A"}],"edges":[]}""";

    [Fact]
    public async Task FingerprintIsStableAndIgnoresBuildOutputAndLocalToken()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var first = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        Assert.Matches("^[0-9a-f]{64}$", first);

        Directory.CreateDirectory(Path.Combine(env.RepoPath, "bin"));
        await File.WriteAllTextAsync(Path.Combine(env.RepoPath, "bin", "out.dll"), "build output");
        Directory.CreateDirectory(Path.Combine(env.RepoPath, ".husky"));
        await File.WriteAllTextAsync(Path.Combine(env.RepoPath, ".husky", ".local-token"), "secret");
        Assert.Equal(first, await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None));
    }

    [Fact]
    public async Task FingerprintChangesOnEditUntrackedFileAndDeletion()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var original = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        var file = Path.Combine(env.RepoPath, "Services", "ReportCache.cs");

        await File.AppendAllTextAsync(file, "// edit\n");
        var edited = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        Assert.NotEqual(original, edited);

        await File.WriteAllTextAsync(Path.Combine(env.RepoPath, "New.cs"), "class New { }");
        var untracked = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        Assert.NotEqual(edited, untracked);

        File.Delete(file);
        Assert.NotEqual(untracked, await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshPublishesOnceAndReusesThePublishedGraph()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new FakeExtractor(ValidGraph);
        var updater = Updater(env, extractor);

        var first = await updater.RefreshAsync(CancellationToken.None);
        var fingerprint = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        Assert.Equal(GraphRefreshStatus.Current, first.Status);
        Assert.Equal(fingerprint, first.Fingerprint);
        Assert.Equal(Path.Combine(env.Paths.GraphsDirectory, $"{fingerprint}-{FakeExtractor.Version}", "graphify-out", "graph.json"), first.Graph!.GraphJsonPath);
        Assert.Equal($"{fingerprint}-{FakeExtractor.Version}", await File.ReadAllTextAsync(Path.Combine(env.Paths.GraphsDirectory, "current.txt")));
        Assert.Equal(fingerprint, updater.ReadCurrent()!.Fingerprint);

        var second = await updater.RefreshAsync(CancellationToken.None);
        Assert.Equal(GraphRefreshStatus.Current, second.Status);
        Assert.Equal(1, extractor.Calls); // reused, not re-extracted
        Assert.Empty(TempFolders(env));
    }

    [Fact]
    public async Task SourceChangeDuringExtraction_IsNotPublished()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new FakeExtractor(ValidGraph, onExtract: repo => File.AppendAllText(Path.Combine(repo, "Program.cs"), "// change\n"));
        var result = await Updater(env, extractor).RefreshAsync(CancellationToken.None);

        Assert.Equal(GraphRefreshStatus.Failed, result.Status);
        Assert.Equal(GraphifyUpdater.SourceChangedReason, result.Reason);
        AssertNothingPublished(env);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"nodes":[],"edges":[]}""")]
    public async Task InvalidGraphOutput_IsNotPublished(string output)
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var result = await Updater(env, new FakeExtractor(output)).RefreshAsync(CancellationToken.None);
        Assert.Equal(GraphRefreshStatus.Failed, result.Status);
        AssertNothingPublished(env);
    }

    [Fact]
    public async Task ExtractorFailure_IsNotPublished()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new FakeExtractor(ValidGraph, onExtract: _ => throw new InvalidOperationException("boom"));
        var result = await Updater(env, extractor).RefreshAsync(CancellationToken.None);
        Assert.Equal(GraphRefreshStatus.Failed, result.Status);
        Assert.Contains("boom", result.Reason);
        AssertNothingPublished(env);
    }

    [Fact]
    public async Task ConcurrentRefreshesNeverOverlap()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new FakeExtractor(ValidGraph, delay: TimeSpan.FromMilliseconds(300));
        var updater = Updater(env, extractor);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => updater.RefreshAsync(CancellationToken.None)));

        Assert.All(results, r => Assert.Equal(GraphRefreshStatus.Current, r.Status));
        Assert.Equal(1, extractor.Calls);
        Assert.Equal(1, extractor.MaxConcurrent);
    }

    // H8 audit: the 5-minute refresh limit and a missing Graphify must end as a failed refresh, never as an exception
    // that fails /analyze with HTTP 500 or stops the background worker (and with it the host).
    [Fact]
    public async Task RefreshTimeout_EndsAsFailed_AndReleasesTheGate()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new FakeExtractor(ValidGraph, delay: TimeSpan.FromSeconds(30));
        var updater = Updater(env, extractor, TimeSpan.FromMilliseconds(300));

        var timedOut = await updater.RefreshAsync(CancellationToken.None);

        Assert.Equal(GraphRefreshStatus.Failed, timedOut.Status);
        Assert.StartsWith(GraphifyUpdater.TimeoutReason, timedOut.Reason);
        AssertNothingPublished(env);

        extractor.Delay = TimeSpan.Zero;
        Assert.Equal(GraphRefreshStatus.Current, (await updater.RefreshAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task WaitingForARunningRefresh_CountsTowardTheTimeout()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new BlockingExtractor();
        var updater = Updater(env, extractor, TimeSpan.FromMilliseconds(500));
        var first = updater.RefreshAsync(CancellationToken.None);
        await extractor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await updater.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(GraphRefreshStatus.Failed, second.Status);
        Assert.Contains("another graph refresh was still running", second.Reason);
        extractor.Release.SetResult();
        Assert.Equal(GraphRefreshStatus.Failed, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        AssertNothingPublished(env);
    }

    [Fact]
    public async Task GraphifyNotInstalled_RefreshFails_WithoutThrowing()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new FakeExtractor(ValidGraph, versionError: new InvalidOperationException("No module named graphify"));

        var result = await Updater(env, extractor).RefreshAsync(CancellationToken.None);

        Assert.Equal(GraphRefreshStatus.Failed, result.Status);
        Assert.Equal($"{GraphifyUpdater.FailedReason}: No module named graphify", result.Reason);
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public async Task Selector_GraphifyNotInstalled_IsNone_WithTheReason()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var updater = Updater(env, new FakeExtractor(ValidGraph, versionError: new InvalidOperationException("No module named graphify")));

        var selection = await new GraphSelector(updater, NullLogger<GraphSelector>.Instance).SelectAsync("fp", CancellationToken.None);

        Assert.Equal((GraphStatuses.None, (PublishedGraph?)null), (selection.Status, selection.Graph));
        Assert.Contains("No module named graphify", selection.Problem);
    }

    [Fact]
    public async Task Selector_RefreshTimeout_UsesTheOlderGraphAsStale()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var older = await Updater(env, new FakeExtractor(ValidGraph)).RefreshAsync(CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(env.RepoPath, "Program.cs"), "// a newer commit\n");
        var fingerprint = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        var slow = Updater(env, new FakeExtractor(ValidGraph, delay: TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(300));

        var selection = await new GraphSelector(slow, NullLogger<GraphSelector>.Instance).SelectAsync(fingerprint, CancellationToken.None);

        Assert.Equal(GraphStatuses.Stale, selection.Status);
        Assert.Equal(older.Fingerprint, selection.Graph!.Fingerprint);
        Assert.Contains(GraphifyUpdater.TimeoutReason, selection.Problem);
    }

    [Fact]
    public async Task Selector_CallerCancellation_StillThrows()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var selector = new GraphSelector(Updater(env, new FakeExtractor(ValidGraph)), NullLogger<GraphSelector>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selector.SelectAsync("fp", cancelled.Token));
    }

    [Fact]
    public async Task BackgroundWorker_SurvivesARefreshTimeout()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var extractor = new FakeExtractor(ValidGraph, delay: TimeSpan.FromSeconds(30));
        var queue = new GraphRefreshQueue();
        using var worker = new GraphRefreshWorker(queue, Updater(env, extractor, TimeSpan.FromMilliseconds(300)), NullLogger<GraphRefreshWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            queue.Request();
            await WaitUntilAsync(() => extractor.Calls == 1 && extractor.Active == 0);
            extractor.Delay = TimeSpan.Zero;
            queue.Request();
            await WaitUntilAsync(() => File.Exists(Path.Combine(env.Paths.GraphsDirectory, "current.txt")));
            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 20 s");
            await Task.Delay(50);
        }
    }

    private static GraphifyUpdater Updater(TestEnvironment env, IGraphExtractor extractor, TimeSpan? refreshTimeout = null) =>
        new(env.Paths, extractor, NullLogger<GraphifyUpdater>.Instance) { RefreshTimeout = refreshTimeout ?? TimeSpan.FromMinutes(5) };

    private static string[] TempFolders(TestEnvironment env) =>
        Directory.Exists(env.Paths.GraphsDirectory) ? Directory.GetDirectories(env.Paths.GraphsDirectory, "tmp-*") : [];

    private static void AssertNothingPublished(TestEnvironment env)
    {
        Assert.Empty(TempFolders(env));
        Assert.False(File.Exists(Path.Combine(env.Paths.GraphsDirectory, "current.txt")));
        Assert.Empty(Directory.Exists(env.Paths.GraphsDirectory) ? Directory.GetDirectories(env.Paths.GraphsDirectory) : []);
    }

    private sealed class FakeExtractor(string graphJson, Action<string>? onExtract = null, TimeSpan delay = default, Exception? versionError = null)
        : IGraphExtractor
    {
        public const string Version = "0.0.0-test";
        private int _active;

        public int Calls { get; private set; }

        public int MaxConcurrent { get; private set; }

        public int Active => Volatile.Read(ref _active);

        public TimeSpan Delay { get; set; } = delay;

        public Task<string> GetVersionAsync(CancellationToken cancellationToken) =>
            versionError is null ? Task.FromResult(Version) : Task.FromException<string>(versionError);

        public async Task ExtractAsync(string repoPath, string outputDirectory, CancellationToken cancellationToken)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _active));
            try
            {
                Calls++;
                await Task.Delay(Delay, cancellationToken);
                onExtract?.Invoke(repoPath);
                var graphFile = Path.Combine(outputDirectory, GraphStructure.GraphifyOutputRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(graphFile)!);
                await File.WriteAllTextAsync(graphFile, graphJson, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    /// <summary>Holds the refresh gate until released, ignoring cancellation like a process that does not stop at once.</summary>
    private sealed class BlockingExtractor : IGraphExtractor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult(FakeExtractor.Version);

        public async Task ExtractAsync(string repoPath, string outputDirectory, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }
}
