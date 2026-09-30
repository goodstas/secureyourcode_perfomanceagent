using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.Reporting;
using SecureYourCode.Agent.StaticAnalysis;
using SecureYourCode.Agent.Verification;

namespace SecureYourCode.Agent.Tests;

public class OrchestratorTests
{
    private static readonly string P1 = CandidateIds.Compute("PERF001", "Services/OrderSummaryService.cs", "OrderSummaryService.BuildSummariesAsync", 17);
    private static readonly string P3 = CandidateIds.Compute("PERF004", "Services/ReportCache.cs", "ReportCache.GetOrAdd", 28);
    private static readonly string P4 = CandidateIds.Compute("LLM-MEM-01", "Services/OrderEvents.cs", "OrderAuditHandler.OrderAuditHandler", 18);

    private static StaticAnalysisResult Baseline() => new(StaticAnalysisStatus.Succeeded, null, "analysis.sarif", [],
    [
        BaselineCandidates.Create(new StaticDiagnostic("PERF001", "m", "Services/OrderSummaryService.cs", 17, 17), "OrderSummaryService.BuildSummariesAsync"),
        BaselineCandidates.Create(new StaticDiagnostic("PERF004", "m", "Services/ReportCache.cs", 28, 28), "ReportCache.GetOrAdd"),
    ]);

    private static string Specialist(string reviewer, params string[] findings) =>
        $$"""{"reviewer":"{{reviewer}}","status":"completed","findings":[{{string.Join(",", findings)}}]}""";

    private static string Enrich(string id, string ruleId, string file, int line, string mechanism) =>
        $$"""{"candidateId":"{{id}}","ruleId":"{{ruleId}}","file":"{{file}}","startLine":{{line}},"endLine":{{line}},"mechanism":"{{mechanism}}","trigger":"t","confidence":"strong","fixDirection":"f"}""";

    private const string P4Finding =
        """{"ruleId":"LLM-MEM-01","file":"Services/OrderEvents.cs","startLine":18,"endLine":18,"mechanism":"scoped handler never unsubscribes","trigger":"each request","confidence":"candidate","fixDirection":"unsubscribe in Dispose"}""";

    /// <summary>A critic that decides "keep" for every candidateId in the prompt unless overridden.</summary>
    private static Func<string, CancellationToken, Task<string>> Critic(Dictionary<string, string>? overrides = null, string benchmarks = "[]", params string[] omit) =>
        (prompt, _) =>
        {
            var ids = Regex.Matches(prompt, "\"candidateId\": \"([0-9a-f]{12})\"").Select(m => m.Groups[1].Value).Distinct().Where(id => !omit.Contains(id));
            var decisions = ids.Select(id => $$"""{"candidateId":"{{id}}","decision":"{{overrides?.GetValueOrDefault(id) ?? "keep"}}","rationale":"r"}""");
            return Task.FromResult($$"""{"reviewer":"VerificationReviewer","status":"completed","decisions":[{{string.Join(",", decisions)}}],"benchmarks":{{benchmarks}}}""");
        };

    private static Func<string, CancellationToken, Task<string>> Reply(string text) => (_, _) => Task.FromResult(text);

    private static Func<string, CancellationToken, Task<string>> Hang =>
        async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            return "";
        };

    private static FakeReviewerClient HappyClient(Dictionary<string, string>? criticOverrides = null, string benchmarks = "[]") => new()
    {
        ["MemoryReviewer"] = [Reply(Specialist("MemoryReviewer", Enrich(P3, "PERF004", "Services/ReportCache.cs", 28, "static cache grows"), P4Finding))],
        ["CpuReviewer"] = [Reply(Specialist("CpuReviewer", Enrich(P1, "PERF001", "Services/OrderSummaryService.cs", 17, "one lookup per order")))],
        ["ConcurrencyReviewer"] = [Reply(Specialist("ConcurrencyReviewer"))],
        ["VerificationReviewer"] = [Critic(criticOverrides, benchmarks)],
    };

    private sealed record Harness(Orchestrator Orchestrator, FakeRepository Repository, FakeStaticAnalysis StaticAnalysis,
        FakeReviewerClient Client, List<Report> Published);

    private static readonly LlmSettings CopilotLlm = LlmSettings.Resolve(new SecureYourCodeOptions());

    private static Harness Create(TestEnvironment env, FakeReviewerClient client, string[]? fingerprints = null,
        Func<StaticAnalysisResult>? analysis = null, OrchestrationTimeouts? timeouts = null, string graphStatus = GraphStatuses.Current,
        IVerificationStage? verifier = null, LlmSettings? llm = null)
    {
        var repository = new FakeRepository(fingerprints ?? ["fp1"]);
        var staticAnalysis = new FakeStaticAnalysis(analysis ?? Baseline);
        var published = new List<Report>();
        var graph = graphStatus == GraphStatuses.None ? null : new PublishedGraph("fp1", "0.9.71", "g", "g/graph.json");
        var orchestrator = new Orchestrator(repository, new FakeGraphs(graphStatus, graph), staticAnalysis, new FakeClientFactory(client),
            verifier ?? new FakeVerifier(), new RecordingPublisher(published), env.Paths, llm ?? CopilotLlm, timeouts ?? OrchestrationTimeouts.Default,
            TimeProvider.System, NullLogger<Orchestrator>.Instance);
        return new Harness(orchestrator, repository, staticAnalysis, client, published);
    }

    [Fact]
    public async Task HappyPath_IsComplete_WithCriticSemanticsUsageAndProvenance()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient(
            new() { [P3] = Decisions.Downgrade, [P4] = Decisions.Remove },
            $$"""[{"candidateId":"{{P1}}","benchmarkKind":"RepositoryCallAmplification","scenario":"OrderCustomerLookup"}]""");
        var h = Create(env, client);

        var report = await h.Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Complete, (string?)null, GraphStatuses.Current), (report.Run.Status, report.Run.Reason, report.Run.GraphStatus));
        Assert.Equal(("fp1", "abc1234def", false, "fp1"),
            (report.Provenance.Fingerprint, report.Provenance.CommitSha, report.Provenance.Dirty, report.Provenance.GraphFingerprint));
        Assert.Equal("Copilot (model auto)", report.Provenance.LlmBackend);
        Assert.Equal("4 " + SessionUsage.CostUnit, report.TokenUsage!.Total.Cost.Display);
        Assert.Equal([P1, P3], report.Findings.Select(f => f.CandidateId));
        Assert.Equal(("strong", "one lookup per order", "keep", "E1"), (report.Findings[0].Confidence, report.Findings[0].Mechanism, report.Findings[0].CriticDecision, report.Findings[0].Evidence));
        Assert.Equal(("low", "downgrade", "E1"), (report.Findings[1].Confidence, report.Findings[1].CriticDecision, report.Findings[1].Evidence));
        var rejected = Assert.Single(report.RejectedCandidates);
        Assert.Equal((P4, Origins.Specialist, "E0"), (rejected.CandidateId, rejected.Origin, rejected.Evidence));
        Assert.All(report.Reviewers, r => Assert.Equal("completed", r.Status));
        Assert.Equal(["MemoryReviewer", "CpuReviewer", "ConcurrencyReviewer", "VerificationReviewer"], report.TokenUsage!.Sessions.Select(s => s.Session));
        Assert.Equal((4, "400"), (report.TokenUsage.Total.ModelCalls, report.TokenUsage.Total.InputTokens.Display));
        Assert.Equal(["fake-model"], report.Run.ModelsUsed);
        Assert.Contains(report.Notes, n => n.StartsWith("Benchmark proposal accepted: RepositoryCallAmplification/OrderCustomerLookup", StringComparison.Ordinal));
        Assert.Same(report, Assert.Single(h.Published));
        Assert.True(File.Exists(Path.Combine(env.Paths.RunsDirectory, report.Run.RunId, "reviewers", "CpuReviewer.reply1.txt")));
    }

    [Fact]
    public async Task ApiKeyMode_RecordsTheBackendInProvenance_AndCostIsNotApplicable()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var options = new SecureYourCodeOptions
        {
            Llm = new LlmOptions { Mode = "ApiKey", Provider = new ProviderOptions { BaseUrl = "http://models.internal:8000/v1", WireModel = "internal-coder-1" } },
        };
        var llm = LlmSettings.Resolve(options, _ => "sk-secret", _ => throw new FileNotFoundException());
        var h = Create(env, HappyClient(), llm: llm);

        var report = await h.Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(RunStatuses.Complete, report.Run.Status);
        Assert.Equal("ApiKey (openai endpoint http://models.internal:8000/v1, model internal-coder-1)", report.Provenance.LlmBackend);
        Assert.Equal(SessionUsage.CostNotApplicable, report.TokenUsage!.Total.Cost.Display);
        Assert.All(report.TokenUsage.Sessions, s => Assert.Equal(SessionUsage.CostNotApplicable, s.Cost.Display));
        Assert.Equal("400", report.TokenUsage.Total.InputTokens.Display);
        Assert.DoesNotContain("sk-secret", System.Text.Json.JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task SpecialistInvalidAfterRepair_IsPartial_AndBaselineIsStillReported()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["CpuReviewer"] = [Reply("I think it's fine."), Reply("still not json")];
        var report = await Create(env, client).Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "specialist_failed: CpuReviewer"), (report.Run.Status, report.Run.Reason));
        var cpu = report.Reviewers.Single(r => r.Reviewer == "CpuReviewer");
        Assert.Equal("failed", cpu.Status);
        Assert.StartsWith("invalid reply after the repair request", cpu.Error);
        Assert.Equal(Consolidator.NotAnalyzed, report.Findings.Single(f => f.CandidateId == P1).Mechanism);
        Assert.Equal(2, client.Sent["CpuReviewer"].Count); // task + one repair request
    }

    [Fact]
    public async Task RepairRequestThatSucceeds_IsComplete()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["ConcurrencyReviewer"] = [Reply("```\nnope\n```"), Reply(Specialist("ConcurrencyReviewer"))];
        var report = await Create(env, client).Orchestrator.RunAsync(CancellationToken.None);
        Assert.Equal(RunStatuses.Complete, report.Run.Status);
        Assert.StartsWith("Your reply could not be used", client.Sent["ConcurrencyReviewer"][1]);
    }

    [Fact]
    public async Task CriticStillIncompleteAfterRepair_IsPartial_MissingDecisionsKeptAsNotReviewed()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["VerificationReviewer"] = [Critic(omit: P3), Critic(omit: P3)];
        var report = await Create(env, client).Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "critic_incomplete"), (report.Run.Status, report.Run.Reason));
        var p3 = report.Findings.Single(f => f.CandidateId == P3);
        Assert.Equal((Decisions.Keep, CriticDecisions.NotReviewed), (p3.CriticDecision, p3.CriticRationale));
        Assert.Contains($"missing decision for {P3}", client.Sent["VerificationReviewer"][1]);
    }

    [Fact]
    public async Task CriticInvalidAfterRepair_IsPartial_AllKeptAsNotReviewed()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["VerificationReviewer"] = [Reply("no"), Reply("still no")];
        var report = await Create(env, client).Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "critic_failed"), (report.Run.Status, report.Run.Reason));
        Assert.Equal(3, report.Findings.Count);
        Assert.Empty(report.RejectedCandidates);
        Assert.All(report.Findings, f => Assert.Equal(CriticDecisions.NotReviewed, f.CriticRationale));
    }

    [Fact]
    public async Task SourceChangedAfterGraphStage_StopsBeforeStaticAnalysis()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var h = Create(env, HappyClient(), fingerprints: ["fp1", "fp2"]);
        var report = await h.Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "source_changed"), (report.Run.Status, report.Run.Reason));
        Assert.Equal(0, h.StaticAnalysis.Calls);
        Assert.Empty(report.Findings);
        Assert.Single(h.Published);
    }

    [Fact]
    public async Task StaticAnalysisFailure_IsFailed_AndNoReviewerRuns()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var h = Create(env, HappyClient(),
            analysis: () => new StaticAnalysisResult(StaticAnalysisStatus.Failed, "build_failed (exit code 1): x", "s", [], []));
        var report = await h.Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Failed, "build_failed (exit code 1): x"), (report.Run.Status, report.Run.Reason));
        Assert.Empty(h.Client.Sent);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task StaticAnalysisNotEvidence_IsPartialSourceChanged()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var report = await Create(env, HappyClient(),
            analysis: () => new StaticAnalysisResult(StaticAnalysisStatus.SourceChanged, StaticAnalysisRunner.SourceChangedReason, "s", [], []))
            .Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "source_changed"), (report.Run.Status, report.Run.Reason));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task SourceChangedByTheEndOfTheRun_IsPartial()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var report = await Create(env, HappyClient(), fingerprints: ["fp1", "fp1", "fp2"]).Orchestrator.RunAsync(CancellationToken.None);
        Assert.Equal((RunStatuses.Partial, "source_changed"), (report.Run.Status, report.Run.Reason));
        Assert.NotEmpty(report.Findings);
    }

    [Fact]
    public async Task CopilotClientFailsToStart_IsFailed_BaselineStillReported()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client.StartFailure = new InvalidOperationException("not signed in");
        var report = await Create(env, client).Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Failed, "orchestration_failed: not signed in"), (report.Run.Status, report.Run.Reason));
        Assert.Equal([P1, P3], report.Findings.Select(f => f.CandidateId));
        Assert.All(report.Findings, f => Assert.Equal((CriticDecisions.NotReviewed, Consolidator.NotAnalyzed), (f.CriticRationale, f.Mechanism)));
    }

    [Fact]
    public async Task RunTimeoutDuringSpecialists_IsFailedTimeout_AndTheReportIsStillPublished()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["CpuReviewer"] = [Hang];
        var h = Create(env, client, timeouts: new OrchestrationTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)));

        var report = await h.Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Failed, "timeout"), (report.Run.Status, report.Run.Reason));
        Assert.Equal("completed", report.Reviewers.Single(r => r.Reviewer == "MemoryReviewer").Status);
        Assert.Equal(("failed", "timeout"), (report.Reviewers.Single(r => r.Reviewer == "CpuReviewer").Status, report.Reviewers.Single(r => r.Reviewer == "CpuReviewer").Error));
        Assert.Single(h.Published);
        Assert.NotNull(report.TokenUsage);
    }

    [Fact]
    public async Task ClientCancellation_IsFailedCancelled()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["MemoryReviewer"] = [Hang];
        using var aborted = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var report = await Create(env, client).Orchestrator.RunAsync(aborted.Token);

        Assert.Equal((RunStatuses.Failed, "cancelled"), (report.Run.Status, report.Run.Reason));
    }

    [Fact]
    public async Task RunTimeoutDuringCritic_IsPartial_NotFailed()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["VerificationReviewer"] = [Hang];
        var report = await Create(env, client, timeouts: new OrchestrationTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)))
            .Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "critic_failed (timeout)"), (report.Run.Status, report.Run.Reason));
        Assert.Equal(3, report.Findings.Count);
        Assert.All(report.Findings, f => Assert.Equal(CriticDecisions.NotReviewed, f.CriticRationale));
    }

    [Fact]
    public async Task SessionTimeout_FailsOnlyThatSpecialist()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var client = HappyClient();
        client["ConcurrencyReviewer"] = [Hang];
        var report = await Create(env, client, timeouts: new OrchestrationTimeouts(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)))
            .Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "specialist_failed: ConcurrencyReviewer"), (report.Run.Status, report.Run.Reason));
        Assert.StartsWith("session timeout", report.Reviewers.Single(r => r.Reviewer == "ConcurrencyReviewer").Error);
        Assert.Equal("completed", report.Reviewers.Single(r => r.Reviewer == "VerificationReviewer").Status);
    }

    [Fact]
    public async Task NoGraph_ReviewersRunWithoutGraphify_AndTheReportSaysSo()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var report = await Create(env, HappyClient(), graphStatus: GraphStatuses.None).Orchestrator.RunAsync(CancellationToken.None);
        Assert.Equal((RunStatuses.Complete, GraphStatuses.None), (report.Run.Status, report.Run.GraphStatus));
        Assert.All(report.Reviewers, r => Assert.False(r.GraphifyAvailable));
        Assert.Contains(report.Notes, n => n.StartsWith("Graphify unavailable to reviewers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerificationAwardsE2_AndAFinalSourceChangeRevertsIt()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var verified = await Create(env, HappyClient(), verifier: new FakeVerifier(awardE2To: P1)).Orchestrator.RunAsync(CancellationToken.None);
        Assert.Equal((RunStatuses.Complete, EvidenceLevels.E2, "verified"),
            (verified.Run.Status, verified.Findings.Single(f => f.CandidateId == P1).Evidence, verified.Findings.Single(f => f.CandidateId == P1).Verification.Status));

        // Fingerprint calls: step 1, step 3, step 6 (the fake verifier does not read it).
        var reverted = await Create(env, HappyClient(), fingerprints: ["fp1", "fp1", "fp2"], verifier: new FakeVerifier(awardE2To: P1))
            .Orchestrator.RunAsync(CancellationToken.None);
        var p1 = reverted.Findings.Single(f => f.CandidateId == P1);
        Assert.Equal((RunStatuses.Partial, "source_changed"), (reverted.Run.Status, reverted.Run.Reason));
        Assert.Equal((EvidenceLevels.E1, "invalidated", "verification invalidated: source changed during run"),
            (p1.Evidence, p1.Verification.Status, p1.Verification.Reason));
    }

    [Fact]
    public async Task VerificationInfrastructureFailure_IsPartial()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var report = await Create(env, HappyClient(), verifier: new FakeVerifier(infrastructureFailure: "template build failed"))
            .Orchestrator.RunAsync(CancellationToken.None);
        Assert.Equal((RunStatuses.Partial, "verification_failed: template build failed"), (report.Run.Status, report.Run.Reason));
    }

    [Fact]
    public async Task RunStoppedDuringVerification_IsPartial_AndUnconfirmedE2IsReverted()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var report = await Create(env, HappyClient(), timeouts: new OrchestrationTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)),
            verifier: new FakeVerifier(awardE2To: P1, hang: true)).Orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal((RunStatuses.Partial, "verification_failed: timeout"), (report.Run.Status, report.Run.Reason));
        var p1 = report.Findings.Single(f => f.CandidateId == P1);
        Assert.Equal((EvidenceLevels.E1, "invalidated"), (p1.Evidence, p1.Verification.Status));
        Assert.StartsWith("verification not confirmed", p1.Verification.Reason);
    }

    [Fact]
    public async Task PublicationFailure_SurfacesWithTheComputedStatus_WhichItNeverChanges()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var repository = new FakeRepository(["fp1"]);
        var orchestrator = new Orchestrator(repository, new FakeGraphs(GraphStatuses.Current, null), new FakeStaticAnalysis(Baseline),
            new FakeClientFactory(HappyClient()), new FakeVerifier(), new ThrowingPublisher(), env.Paths, CopilotLlm, OrchestrationTimeouts.Default,
            TimeProvider.System, NullLogger<Orchestrator>.Instance);

        var error = await Assert.ThrowsAsync<ReportPublicationException>(() => orchestrator.RunAsync(CancellationToken.None));

        Assert.Equal(RunStatuses.Complete, error.Report.Run.Status);
        Assert.Contains("disk full", error.Message);
    }

    [Fact]
    public async Task GateAllowsOneRunAtATime()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var orchestrator = Create(env, HappyClient()).Orchestrator;
        Assert.True(orchestrator.TryEnterGate());
        Assert.False(orchestrator.TryEnterGate());
        orchestrator.ExitGate();
        Assert.True(orchestrator.TryEnterGate());
        orchestrator.ExitGate();
    }

    private sealed class FakeRepository(string[] fingerprints) : IRepositorySnapshot
    {
        private int _calls;

        public Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken) =>
            Task.FromResult(fingerprints[Math.Min(_calls++, fingerprints.Length - 1)]);

        public Task<RepoState> ReadStateAsync(CancellationToken cancellationToken) => Task.FromResult(new RepoState("abc1234def", false));
    }

    private sealed class FakeGraphs(string status, PublishedGraph? graph) : IGraphSelector
    {
        public Task<(string Status, PublishedGraph? Graph)> SelectAsync(string analysisFingerprint, CancellationToken cancellationToken) =>
            Task.FromResult((status, graph));
    }

    private sealed class FakeStaticAnalysis(Func<StaticAnalysisResult> result) : IStaticAnalysis
    {
        public int Calls { get; private set; }

        public Task<StaticAnalysisResult> RunAsync(string analysisFingerprint, string runDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result());
        }
    }

    /// <summary>Optionally awards E2 to one candidate (then hangs, to simulate a stop during verification) or reports a failure.</summary>
    private sealed class FakeVerifier(string? awardE2To = null, string? infrastructureFailure = null, bool hang = false) : IVerificationStage
    {
        public async Task<VerificationSummary> VerifyAsync(IReadOnlyList<Candidate> candidates,
            IReadOnlyList<(BenchmarkProposal Proposal, BenchmarkTemplate Template)> acceptedProposals,
            string analysisFingerprint, string runDirectory, CancellationToken cancellationToken)
        {
            if (candidates.FirstOrDefault(c => c.CandidateId == awardE2To) is { } candidate)
            {
                candidate.Verification = new VerificationResult("verified", "RepositoryCallAmplification/OrderCustomerLookup", "verified", PreviousEvidence: candidate.Evidence);
                candidate.Evidence = EvidenceLevels.E2;
            }

            if (hang)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }

            var summary = new VerificationSummary { InfrastructureFailure = infrastructureFailure };
            return summary;
        }
    }

    private sealed class ThrowingPublisher : IReportPublisher
    {
        public Task PublishAsync(Report report, string runDirectory, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("disk full"));
    }

    private sealed class RecordingPublisher(List<Report> published) : IReportPublisher
    {
        public Task PublishAsync(Report report, string runDirectory, CancellationToken cancellationToken)
        {
            published.Add(report);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClientFactory(FakeReviewerClient client) : IReviewerClientFactory
    {
        public IReviewerClient Create() => client;
    }

    /// <summary>Scripted replies per reviewer, consumed in order; records every prompt it receives.</summary>
    private sealed class FakeReviewerClient : Dictionary<string, List<Func<string, CancellationToken, Task<string>>>>, IReviewerClient
    {
        public Exception? StartFailure { get; set; }

        public Dictionary<string, List<string>> Sent { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);

        public Task<IReviewerSession> CreateSessionAsync(ReviewerDefinition reviewer, PublishedGraph? graph, CancellationToken cancellationToken)
        {
            var sent = Sent.TryGetValue(reviewer.Name, out var list) ? list : Sent[reviewer.Name] = [];
            return Task.FromResult<IReviewerSession>(new FakeSession(new Queue<Func<string, CancellationToken, Task<string>>>(this[reviewer.Name]), sent));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(Queue<Func<string, CancellationToken, Task<string>>> script, List<string> sent) : IReviewerSession
    {
        private readonly List<UsageRecord> _usage = [];

        public IReadOnlyList<UsageRecord> Usage => _usage;

        public int GraphifyCalls => 0;

        public IReadOnlyList<string> DeniedToolRequests => [];

        public async Task<string> SendAsync(string prompt, CancellationToken cancellationToken)
        {
            sent.Add(prompt);
            _usage.Add(new UsageRecord("fake-model", 100, 10, 1));
            return await script.Dequeue()(prompt, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
