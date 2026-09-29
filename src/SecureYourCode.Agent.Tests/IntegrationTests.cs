using Microsoft.Extensions.Logging.Abstractions;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.StaticAnalysis;

namespace SecureYourCode.Agent.Tests;

/// <summary>
/// Real tools, no credentials: dotnet build with the Release analyzer, the pinned Graphify venv, and Husky.Net.
/// Prerequisites come from tools/setup.py (Release analyzer build, &lt;user StateRoot&gt;/graphify-venv).
/// </summary>
public class IntegrationTests
{
    [Fact]
    public async Task StaticAnalysis_CreatesBaselineCandidatesForTheDemoPositives()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync(analyzerReference: true);
        var fingerprint = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        var runner = new StaticAnalysisRunner(env.Paths, NullLogger<StaticAnalysisRunner>.Instance);

        var result = await runner.RunAsync(fingerprint, Path.Combine(env.Paths.RunsDirectory, "test-run"), CancellationToken.None);

        Assert.Equal(StaticAnalysisStatus.Succeeded, result.Status);
        var actual = result.BaselineCandidates
            .Select(c => (c.RuleId, c.File, c.StartLine, c.EnclosingSymbol, c.Category))
            .OrderBy(c => c.File, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                ("PERF001", "Services/InvoiceService.cs", 19, "InvoiceService.BuildInvoiceLinesAsync", Categories.Cpu),
                ("PERF003", "Services/NotificationService.cs", 13, "NotificationService.NotifyAllAsync", Categories.Concurrency),
                ("PERF001", "Services/OrderSummaryService.cs", 17, "OrderSummaryService.BuildSummariesAsync", Categories.Cpu),
                ("PERF004", "Services/ReportCache.cs", 28, "ReportCache.GetOrAdd", Categories.Memory),
            ],
            actual);
        Assert.All(result.BaselineCandidates, c => Assert.Equal((Origins.Roslyn, Confidences.Candidate, EvidenceLevels.E1), (c.Origin, c.Confidence, c.Evidence)));
        Assert.Equal(fingerprint, await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None)); // build output is ignored
    }

    [Fact]
    public async Task StaticAnalysis_SourceChangedBeforeBuild_IsNotEvidence()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync(analyzerReference: true);
        var runner = new StaticAnalysisRunner(env.Paths, NullLogger<StaticAnalysisRunner>.Instance);

        var result = await runner.RunAsync(new string('0', 64), Path.Combine(env.Paths.RunsDirectory, "test-run"), CancellationToken.None);

        Assert.Equal(StaticAnalysisStatus.SourceChanged, result.Status);
        Assert.Equal(StaticAnalysisRunner.SourceChangedReason, result.Reason);
        Assert.Empty(result.BaselineCandidates);
    }

    [Fact]
    public async Task StaticAnalysis_BuildFailure_IsFailedNotZeroFindings()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync(analyzerReference: true);
        await File.WriteAllTextAsync(Path.Combine(env.RepoPath, "Broken.cs"), "this is not C#");
        var fingerprint = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        var runner = new StaticAnalysisRunner(env.Paths, NullLogger<StaticAnalysisRunner>.Instance);

        var result = await runner.RunAsync(fingerprint, Path.Combine(env.Paths.RunsDirectory, "test-run"), CancellationToken.None);

        Assert.Equal(StaticAnalysisStatus.Failed, result.Status);
        Assert.StartsWith("build_failed", result.Reason);
        Assert.Empty(result.BaselineCandidates);
    }

    [Fact]
    public async Task GraphifyExtraction_PublishesAValidGraphOutsideRepoPath()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var userState = StatePaths.Resolve(new SecureYourCodeOptions());
        Assert.True(File.Exists(userState.GraphifyCli), "Run tools/setup.py first (Graphify venv).");
        var extractor = new GraphifyCliExtractor(userState);
        var updater = new GraphifyUpdater(env.Paths, extractor, NullLogger<GraphifyUpdater>.Instance);
        var statusBefore = await Git.RunAsync(env.RepoPath, CancellationToken.None, "status", "--porcelain", "--ignored");

        var result = await updater.RefreshAsync(CancellationToken.None);

        Assert.Equal(GraphRefreshStatus.Current, result.Status);
        Assert.True(GraphStructure.IsValidGraphFile(result.Graph!.GraphJsonPath));
        Assert.Equal(await extractor.GetVersionAsync(CancellationToken.None), result.Graph.GraphifyVersion);
        Assert.False(Directory.Exists(Path.Combine(env.RepoPath, "graphify-out")));
        Assert.Equal(statusBefore, await Git.RunAsync(env.RepoPath, CancellationToken.None, "status", "--porcelain", "--ignored"));
    }

    [Fact]
    public async Task HookInstaller_InstallsCommitsAndRepairs()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var token = LocalAccessToken.Ensure(env.Paths.AccessTokenFile);
        var installer = new GitHookInstaller(env.Paths, token, NullLogger<GitHookInstaller>.Instance);
        var husky = Path.Combine(env.RepoPath, ".husky");

        await installer.EnsureHookInstalledAsync(CancellationToken.None);
        var commitsAfterInstall = await CommitCount(env);
        Assert.Equal(2, commitsAfterInstall);
        Assert.Equal(await Template(env, HostPlatform.HookTaskRunnerTemplate), await File.ReadAllTextAsync(Path.Combine(husky, "task-runner.json")));
        Assert.Equal((await Template(env, "post-commit")).ReplaceLineEndings("\n"), await File.ReadAllTextAsync(Path.Combine(husky, "post-commit")));
        Assert.Equal(token.Value, await File.ReadAllTextAsync(Path.Combine(husky, ".local-token")));
        Assert.Contains("\"0.9.1\"", await File.ReadAllTextAsync(Path.Combine(env.RepoPath, ".config", "dotnet-tools.json")));
        Assert.Equal(".husky", (await Git.RunAsync(env.RepoPath, CancellationToken.None, "config", "--local", "core.hooksPath")).Trim());
        Assert.Empty((await Git.RunAsync(env.RepoPath, CancellationToken.None, "status", "--porcelain")).Trim());

        await installer.EnsureHookInstalledAsync(CancellationToken.None);
        Assert.Equal(commitsAfterInstall, await CommitCount(env)); // idempotent

        // Interrupted/partial state: files missing from the working tree are restored to their committed content.
        File.Delete(Path.Combine(husky, "task-runner.json"));
        File.Delete(Path.Combine(husky, ".local-token"));
        await installer.EnsureHookInstalledAsync(CancellationToken.None);
        Assert.Equal(commitsAfterInstall, await CommitCount(env)); // restored to HEAD content: nothing to commit
        Assert.Equal(token.Value, await File.ReadAllTextAsync(Path.Combine(husky, ".local-token")));
        Assert.Empty((await Git.RunAsync(env.RepoPath, CancellationToken.None, "status", "--porcelain")).Trim());

        // Wrong committed content: repaired and the repair committed.
        await File.WriteAllTextAsync(Path.Combine(husky, "task-runner.json"), """{ "tasks": [] }""");
        await Git.RunAsync(env.RepoPath, CancellationToken.None, "add", ".husky/task-runner.json");
        await Git.CommitAsync(env.RepoPath, "simulate a broken hook", CancellationToken.None);
        await installer.EnsureHookInstalledAsync(CancellationToken.None);
        Assert.Equal(commitsAfterInstall + 2, await CommitCount(env));
        Assert.Equal(await Template(env, HostPlatform.HookTaskRunnerTemplate), await File.ReadAllTextAsync(Path.Combine(husky, "task-runner.json")));
        Assert.Empty((await Git.RunAsync(env.RepoPath, CancellationToken.None, "status", "--porcelain")).Trim());
    }

    private static async Task<int> CommitCount(TestEnvironment env) =>
        int.Parse((await Git.RunAsync(env.RepoPath, CancellationToken.None, "rev-list", "--count", "HEAD")).Trim());

    private static Task<string> Template(TestEnvironment env, string name) =>
        File.ReadAllTextAsync(Path.Combine(env.Paths.HookTemplatesDirectory, name));
}
