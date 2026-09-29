using Microsoft.Extensions.Logging.Abstractions;
using SecureYourCode.Agent.Graph;

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

    private static GraphifyUpdater Updater(TestEnvironment env, IGraphExtractor extractor) =>
        new(env.Paths, extractor, NullLogger<GraphifyUpdater>.Instance);

    private static string[] TempFolders(TestEnvironment env) =>
        Directory.Exists(env.Paths.GraphsDirectory) ? Directory.GetDirectories(env.Paths.GraphsDirectory, "tmp-*") : [];

    private static void AssertNothingPublished(TestEnvironment env)
    {
        Assert.Empty(TempFolders(env));
        Assert.False(File.Exists(Path.Combine(env.Paths.GraphsDirectory, "current.txt")));
        Assert.Empty(Directory.Exists(env.Paths.GraphsDirectory) ? Directory.GetDirectories(env.Paths.GraphsDirectory) : []);
    }

    private sealed class FakeExtractor(string graphJson, Action<string>? onExtract = null, TimeSpan delay = default) : IGraphExtractor
    {
        public const string Version = "0.0.0-test";
        private int _active;

        public int Calls { get; private set; }

        public int MaxConcurrent { get; private set; }

        public Task<string> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult(Version);

        public async Task ExtractAsync(string repoPath, string outputDirectory, CancellationToken cancellationToken)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _active));
            try
            {
                Calls++;
                await Task.Delay(delay, cancellationToken);
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
}
