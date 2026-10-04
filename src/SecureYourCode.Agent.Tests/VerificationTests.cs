using Microsoft.Extensions.Logging.Abstractions;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.StaticAnalysis;
using SecureYourCode.Agent.Verification;

namespace SecureYourCode.Agent.Tests;

public class BenchmarkVerifierTests
{
    private static readonly BenchmarkTemplate Amplification = BenchmarkTemplates.Find("RepositoryCallAmplification", "OrderCustomerLookup")!;
    private static readonly BenchmarkTemplate Growth = BenchmarkTemplates.Find("CollectionGrowth", "ReportCache")!;
    private static readonly BenchmarkTemplate FanOut = BenchmarkTemplates.Find("TaskFanOut", "NotificationRecipients")!;

    private static Candidate P1(string decision = Decisions.Keep) => Make("PERF001", "Services/OrderSummaryService.cs", 17, "OrderSummaryService.BuildSummariesAsync", decision);
    private static Candidate P5() => Make("PERF001", "Services/InvoiceService.cs", 19, "InvoiceService.BuildInvoiceLinesAsync", Decisions.Keep);
    private static Candidate P3() => Make("PERF004", "Services/ReportCache.cs", 28, "ReportCache.GetOrAdd", Decisions.Keep);
    private static Candidate P2() => Make("PERF003", "Services/NotificationService.cs", 13, "NotificationService.NotifyAllAsync", Decisions.Keep);

    private static Candidate Make(string rule, string file, int line, string symbol, string decision)
    {
        var candidate = BaselineCandidates.Create(new StaticDiagnostic(rule, "m", file, line, line), symbol);
        candidate.CriticDecision = decision;
        return candidate;
    }

    private static (BenchmarkProposal, BenchmarkTemplate) Proposal(Candidate c, BenchmarkTemplate t) =>
        (new BenchmarkProposal(c.CandidateId, t.Kind, t.Scenario), t);

    /// <summary>Observations that scale linearly with n (every rule is met).</summary>
    private static Func<BenchmarkTemplate, BenchmarkRun> Linear =>
        t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, n))]);

    private static BenchmarkProcess Process(BenchmarkTemplate t, int n, double value, string? metric = null, int? reportedN = null) =>
        new(n, 0, $$"""{"n":{{reportedN ?? n}},"metric":"{{metric ?? t.Metric}}","value":{{value}}}""" + "\n", "");

    private static async Task<(VerificationSummary Summary, FakeRunner Runner)> Verify(
        IReadOnlyList<Candidate> candidates,
        Func<BenchmarkTemplate, BenchmarkRun> result,
        IReadOnlyList<(BenchmarkProposal, BenchmarkTemplate)>? accepted = null,
        string[]? fingerprints = null,
        TimeSpan? delay = null,
        TimeSpan? setTimeout = null)
    {
        var runner = new FakeRunner(result, delay ?? TimeSpan.Zero);
        var verifier = new BenchmarkVerifier(runner, new SequenceRepository(fingerprints ?? ["fp"]), NullLogger<BenchmarkVerifier>.Instance)
        {
            SetTimeout = setTimeout ?? TimeSpan.FromMinutes(3),
        };
        var summary = await verifier.VerifyAsync(candidates, accepted ?? [], "fp", Path.Combine(Path.GetTempPath(), "run-1"), CancellationToken.None);
        return (summary, runner);
    }

    [Fact]
    public async Task RequiredTemplateRunsForTheSeamWithoutAProposal_AndAwardsE2()
    {
        var p1 = P1();
        var (summary, runner) = await Verify([p1], Linear);

        Assert.Equal(["RepositoryCallAmplification"], runner.Calls);
        Assert.Equal((EvidenceLevels.E2, "verified", "RepositoryCallAmplification/OrderCustomerLookup", "E1"),
            (p1.Evidence, p1.Verification.Status, p1.Verification.Template, p1.Verification.PreviousEvidence));
        Assert.StartsWith("verified by host benchmark template RepositoryCallAmplification/OrderCustomerLookup", p1.Verification.Reason);
        Assert.Equal([10d, 100d, 1000d], p1.Verification.Observations!.Select(o => o.Value));
        Assert.Null(summary.InfrastructureFailure);
        Assert.Contains(summary.Notes, n => n.Contains("demo project only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DowngradedSeamCandidateStillRunsRequired_RemovedNeverRuns()
    {
        var downgraded = P1(Decisions.Downgrade);
        downgraded.Confidence = Confidences.Low;
        await Verify([downgraded], Linear);
        Assert.Equal((EvidenceLevels.E2, Confidences.Low), (downgraded.Evidence, downgraded.Confidence)); // independent axes

        var removed = P1(Decisions.Remove);
        var (_, runner) = await Verify([removed], Linear);
        Assert.Empty(runner.Calls);
        Assert.Equal(("not_run", "rejected by the critic", EvidenceLevels.E1), (removed.Verification.Status, removed.Verification.Reason, removed.Evidence));
    }

    [Fact]
    public async Task OtherPerf001Method_IsNeverVerifiedByTheP1Template()
    {
        var p5 = P5();
        var (_, runner) = await Verify([p5], Linear, accepted: [Proposal(p5, Amplification)]);
        Assert.Empty(runner.Calls);
        Assert.Equal((EvidenceLevels.E1, "not_run"), (p5.Evidence, p5.Verification.Status));
    }

    [Fact]
    public async Task OptionalTemplatesRunOnlyWhenAccepted_AndAPairNeverRunsTwice()
    {
        var (p1, p2, p3) = (P1(), P2(), P3());
        var (_, runner) = await Verify([p1, p2, p3], Linear, accepted: [Proposal(p1, Amplification), Proposal(p3, Growth)]);

        Assert.Equal(["RepositoryCallAmplification", "CollectionGrowth"], runner.Calls);
        Assert.Equal((EvidenceLevels.E2, EvidenceLevels.E1, EvidenceLevels.E2), (p1.Evidence, p2.Evidence, p3.Evidence));
        Assert.Equal("not_run", p2.Verification.Status);
    }

    [Fact]
    public async Task AcceptanceRuleNotMet_KeepsE1_WithTheReason()
    {
        var p1 = P1();
        await Verify([p1], t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, 1))]));
        Assert.Equal((EvidenceLevels.E1, "not_verified"), (p1.Evidence, p1.Verification.Status));
        Assert.Equal("benchmark ran; acceptance rule not met: value(10)=1 < 0.9·10", p1.Verification.Reason);

        var amplified = P1();
        var values = new Dictionary<int, double> { [10] = 20, [100] = 100, [1000] = 950 };
        await Verify([amplified], t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, values[n]))]));
        Assert.Equal(EvidenceLevels.E1, amplified.Evidence);
        Assert.EndsWith("value(1000)=950 < 50·value(10)=1000", amplified.Verification.Reason);
    }

    public static TheoryData<string, Func<BenchmarkTemplate, BenchmarkRun>, string> InvalidOutputs => new()
    {
        { "wrong n", t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, n, reportedN: n + 1))]), "reports a different n" },
        { "wrong metric", t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, n, metric: "calls"))]), "expected 'repository calls'" },
        { "negative", t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, -1))]), "non-negative" },
        { "two lines", t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, n) with { StandardOutput = "{}\n{}\n" })]), "printed 2 observations" },
        { "not json", t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => Process(t, n, n) with { StandardOutput = "hello" })]), "not valid JSON" },
        { "crash", t => new BenchmarkRun(null, [.. BenchmarkTemplates.Sizes.Select(n => new BenchmarkProcess(n, 3, "", "boom"))]), "exited with code 3" },
        { "build", _ => new BenchmarkRun("template build failed (exit code 1): error CS0246", []), "template build failed" },
    };

    [Theory]
    [MemberData(nameof(InvalidOutputs))]
    public async Task InvalidOutputOrBuildFailure_IsAnInfrastructureFailure_NeverE2(string label, Func<BenchmarkTemplate, BenchmarkRun> result, string expected)
    {
        var p1 = P1();
        var (summary, _) = await Verify([p1], result);
        Assert.Equal((EvidenceLevels.E1, "failed"), (p1.Evidence, p1.Verification.Status));
        Assert.True(p1.Verification.Reason.Contains(expected, StringComparison.Ordinal), $"{label}: {p1.Verification.Reason}");
        Assert.Contains(expected, summary.InfrastructureFailure);
    }

    [Fact]
    public async Task BenchmarkSetTimeout_IsAnInfrastructureFailure()
    {
        var p1 = P1();
        var (summary, _) = await Verify([p1], Linear, delay: TimeSpan.FromSeconds(30), setTimeout: TimeSpan.FromMilliseconds(300));
        Assert.Equal((EvidenceLevels.E1, "failed"), (p1.Evidence, p1.Verification.Status));
        Assert.Contains("timed out", summary.InfrastructureFailure);
    }

    [Fact]
    public async Task RunnerException_IsAnInfrastructureFailure_NotARunCrash()
    {
        var p1 = P1();
        var (summary, _) = await Verify([p1], _ => throw new System.ComponentModel.Win32Exception("dotnet could not be started"));
        Assert.Equal((EvidenceLevels.E1, "failed"), (p1.Evidence, p1.Verification.Status));
        Assert.Contains("benchmark infrastructure error: dotnet could not be started", summary.InfrastructureFailure);
    }

    [Fact]
    public async Task SourceChangedBeforeVerification_SkipsEverything()
    {
        var p1 = P1();
        var (summary, runner) = await Verify([p1], Linear, fingerprints: ["changed"]);
        Assert.Empty(runner.Calls);
        Assert.Equal(("skipped", BenchmarkVerifier.SourceChangedBeforeVerification), (p1.Verification.Status, p1.Verification.Reason));
        Assert.Contains(summary.Notes, n => n.Contains(BenchmarkVerifier.SourceChangedBeforeVerification, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceChangedAroundTheBenchmark_DiscardsTheResult()
    {
        var p1 = P1();
        await Verify([p1], Linear, fingerprints: ["fp", "fp", "changed"]); // before verification, before set, after set
        Assert.Equal((EvidenceLevels.E1, "not_verified"), (p1.Evidence, p1.Verification.Status));
        Assert.Contains("source changed around the benchmark", p1.Verification.Reason);
    }

    private sealed class FakeRunner(Func<BenchmarkTemplate, BenchmarkRun> result, TimeSpan delay) : IBenchmarkRunner
    {
        public List<string> Calls { get; } = [];

        public async Task<BenchmarkRun> RunAsync(BenchmarkTemplate template, string runId, string candidateId, CancellationToken cancellationToken)
        {
            Calls.Add(template.Kind);
            await Task.Delay(delay, cancellationToken);
            return result(template);
        }
    }

    private sealed class SequenceRepository(string[] fingerprints) : IRepositorySnapshot
    {
        private int _calls;

        public Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken) =>
            Task.FromResult(fingerprints[Math.Min(_calls++, fingerprints.Length - 1)]);

        public Task<RepoState> ReadStateAsync(CancellationToken cancellationToken) => Task.FromResult(new RepoState("sha", false));
    }
}

/// <summary>Real templates, real builds, real fresh processes against a throwaway demo repo (no credentials).</summary>
public class BenchmarkIntegrationTests
{
    private static BenchmarkVerifier Verifier(TestEnvironment env) =>
        new(new DotnetBenchmarkRunner(env.Paths), new RepositorySnapshot(env.Paths), NullLogger<BenchmarkVerifier>.Instance);

    private static Candidate Seam(string rule, string file, int line, string symbol)
    {
        var candidate = BaselineCandidates.Create(new StaticDiagnostic(rule, "m", file, line, line), symbol);
        candidate.CriticDecision = Decisions.Keep;
        return candidate;
    }

    [Fact]
    public async Task AllThreeTemplates_ReachE2_OnTheDemoSeams()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var p1 = Seam("PERF001", "Services/OrderSummaryService.cs", 17, "OrderSummaryService.BuildSummariesAsync");
        var p3 = Seam("PERF004", "Services/ReportCache.cs", 28, "ReportCache.GetOrAdd");
        var p2 = Seam("PERF003", "Services/NotificationService.cs", 13, "NotificationService.NotifyAllAsync");
        var fingerprint = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);
        var accepted = new List<(BenchmarkProposal, BenchmarkTemplate)>
        {
            (new BenchmarkProposal(p3.CandidateId, "CollectionGrowth", "ReportCache"), BenchmarkTemplates.Find("CollectionGrowth", "ReportCache")!),
            (new BenchmarkProposal(p2.CandidateId, "TaskFanOut", "NotificationRecipients"), BenchmarkTemplates.Find("TaskFanOut", "NotificationRecipients")!),
        };

        var summary = await Verifier(env).VerifyAsync([p1, p3, p2], accepted, fingerprint, Path.Combine(env.Paths.RunsDirectory, "it-run"), CancellationToken.None);

        Assert.Null(summary.InfrastructureFailure);
        foreach (var candidate in new[] { p1, p3, p2 })
        {
            Assert.True(candidate.Evidence == EvidenceLevels.E2, $"{candidate.EnclosingSymbol}: {candidate.Verification.Reason}");
            Assert.Equal([10d, 100d, 1000d], candidate.Verification.Observations!.Select(o => o.Value)); // fresh process per n
        }

        Assert.Empty((await Git.RunAsync(env.RepoPath, CancellationToken.None, "status", "--porcelain")).Trim()); // only ignored build output
        Assert.Equal(fingerprint, await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None));
    }

    [Fact]
    public async Task BatchedSeam_DoesNotMeetTheAcceptanceRule()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var file = Path.Combine(env.RepoPath, "Services", "OrderSummaryService.cs");
        var source = await File.ReadAllTextAsync(file);
        var batched = source
            .Replace("var summaries = new List<OrderSummary>(orders.Count);",
                "var customers = await _customers.GetByIdsAsync(orders.Select(o => o.CustomerId).ToArray());\n        var summaries = new List<OrderSummary>(orders.Count);")
            .Replace("var customer = await _customers.GetByIdAsync(order.CustomerId);", "var customer = customers[order.CustomerId];");
        Assert.NotEqual(source, batched);
        await File.WriteAllTextAsync(file, batched);
        var p1 = Seam("PERF001", "Services/OrderSummaryService.cs", 17, "OrderSummaryService.BuildSummariesAsync");
        var fingerprint = await RepoFingerprint.ComputeAsync(env.RepoPath, CancellationToken.None);

        var summary = await Verifier(env).VerifyAsync([p1], [], fingerprint, Path.Combine(env.Paths.RunsDirectory, "it-run"), CancellationToken.None);

        Assert.Null(summary.InfrastructureFailure);
        Assert.Equal((EvidenceLevels.E1, "not_verified"), (p1.Evidence, p1.Verification.Status));
        Assert.Equal([1d, 1d, 1d], p1.Verification.Observations!.Select(o => o.Value));
        Assert.Equal("benchmark ran; acceptance rule not met: value(10)=1 < 0.9·10", p1.Verification.Reason);
    }
}
