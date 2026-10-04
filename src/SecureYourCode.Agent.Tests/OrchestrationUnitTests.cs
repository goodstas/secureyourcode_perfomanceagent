using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.Reporting;
using SecureYourCode.Agent.StaticAnalysis;

namespace SecureYourCode.Agent.Tests;

public class ReviewerRepliesTests
{
    private const string Valid = """
        {"reviewer":"CpuReviewer","status":"completed","findings":[
          {"candidateId":"abc","ruleId":"PERF001","file":"Services/A.cs","startLine":3,"endLine":4,"enclosingSymbol":"A.B",
           "mechanism":"m","trigger":"t","confidence":"strong","fixDirection":"f"}]}
        """;

    [Fact]
    public void ParsesPlainFencedAndProseWrappedJson()
    {
        foreach (var reply in new[] { Valid, $"```json\n{Valid}\n```", $"Here is my review:\n{Valid}\nDone." })
        {
            Assert.True(ReviewerReplies.TryParseSpecialist(reply, "CpuReviewer", out var parsed, out var error), error);
            var finding = Assert.Single(parsed!.Findings);
            Assert.Equal(("abc", "PERF001", 3, 4, "strong", (string?)null), (finding.CandidateId, finding.RuleId, finding.StartLine, finding.EndLine, finding.Confidence, finding.GraphPath));
        }
    }

    [Theory]
    [InlineData("not json at all", "not a single JSON object")]
    [InlineData("""{"reviewer":"MemoryReviewer","status":"completed","findings":[]}""", "\"reviewer\" must be")]
    [InlineData("""{"reviewer":"CpuReviewer","status":"completed"}""", "\"findings\" must be an array")]
    [InlineData("""{"reviewer":"CpuReviewer","status":"completed","findings":[{"ruleId":"PERF001"}]}""", "findings[0].file")]
    [InlineData("""{"reviewer":"CpuReviewer","status":"completed","findings":[{"ruleId":"R","file":"f","startLine":"3","endLine":3,"mechanism":"m","trigger":"t","confidence":"strong","fixDirection":"f"}]}""", "startLine must be an integer")]
    [InlineData("""{"reviewer":"CpuReviewer","status":"completed","findings":[{"ruleId":"R","file":"f","startLine":3,"endLine":3,"mechanism":"m","trigger":"t","confidence":"confirmed","fixDirection":"f"}]}""", "confidence must be")]
    public void RejectsInvalidShapesWithARepairableMessage(string reply, string expectedError)
    {
        Assert.False(ReviewerReplies.TryParseSpecialist(reply, "CpuReviewer", out _, out var error));
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void ParsesCriticDecisionsRawAndBenchmarks()
    {
        const string reply = """
            {"reviewer":"VerificationReviewer","status":"completed",
             "decisions":[{"candidateId":"a","decision":"keep","rationale":"r"},{"candidateId":"b","decision":"maybe"}],
             "benchmarks":[{"candidateId":"a","benchmarkKind":"TaskFanOut","scenario":"NotificationRecipients"}]}
            """;
        Assert.True(ReviewerReplies.TryParseCritic(reply, out var parsed, out _));
        Assert.Equal(2, parsed!.Decisions.Count); // kept raw; the host validates them
        Assert.Equal(new BenchmarkProposal("a", "TaskFanOut", "NotificationRecipients"), Assert.Single(parsed.Benchmarks));
    }
}

public class ConsolidatorTests
{
    private static Candidate Baseline(string ruleId, string file, int line, string symbol) =>
        BaselineCandidates.Create(new StaticDiagnostic(ruleId, "msg", file, line, line), symbol);

    private static SpecialistFinding Finding(string? id, string ruleId, string file, int start, int end, string mechanism = "mechanism",
        string confidence = "candidate", string? symbol = null) =>
        new(id, ruleId, file, start, end, symbol, mechanism, "trigger", null, confidence, "fix");

    private static (List<Candidate> Result, Dictionary<string, SpecialistDiagnostics> Notes) Run(
        TestEnvironment env, IReadOnlyList<Candidate> baseline, params (ReviewerDefinition, SpecialistFinding[])[] replies)
    {
        var notes = new Dictionary<string, SpecialistDiagnostics>();
        var result = new Consolidator(env.RepoPath).Consolidate(
            baseline, [.. replies.Select(r => (r.Item1, new SpecialistReply(r.Item1.Name, r.Item2)))], notes);
        return (result, notes);
    }

    [Fact]
    public async Task EnrichesByIdOrRange_NeverChangingIdentity()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var p1 = Baseline("PERF001", "Services/OrderSummaryService.cs", 17, "OrderSummaryService.BuildSummariesAsync");
        var p3 = Baseline("PERF004", "Services/ReportCache.cs", 28, "ReportCache.GetOrAdd");
        var p1Id = p1.CandidateId;

        var (result, notes) = Run(env, [p1, p3],
            (Reviewers.Cpu, [Finding(p1Id, "PERF001", "Services/OrderSummaryService.cs", 99, 99, "per-order lookup", "strong", "Wrong.Symbol")]),
            (Reviewers.Memory, [Finding(null, "PERF004", "Services/ReportCache.cs", 21, 33, "static cache grows")]));

        Assert.Equal(2, result.Count);
        Assert.Equal((p1Id, 17, 17, "OrderSummaryService.BuildSummariesAsync", "strong", "per-order lookup", "E1"),
            (p1.CandidateId, p1.StartLine, p1.EndLine, p1.EnclosingSymbol, p1.Confidence, p1.Mechanism, p1.Evidence));
        Assert.Contains(notes["CpuReviewer"].Notes, n => n.StartsWith("specialist_identity_mismatch", StringComparison.Ordinal));
        Assert.Equal("static cache grows", p3.Mechanism); // matched by same rule + file + range containing line 28
        Assert.Equal(["MemoryReviewer"], p3.Reviewers);
    }

    [Fact]
    public async Task SeveralSpecialists_LongerTextAndHigherConfidenceWin_UnenrichedIsMarked()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var p1 = Baseline("PERF001", "Services/OrderSummaryService.cs", 17, "OrderSummaryService.BuildSummariesAsync");
        var p3 = Baseline("PERF004", "Services/ReportCache.cs", 28, "ReportCache.GetOrAdd");

        Run(env, [p1, p3],
            (Reviewers.Cpu, [Finding(p1.CandidateId, "PERF001", "Services/OrderSummaryService.cs", 17, 17, "a much longer mechanism text", "candidate")]),
            (Reviewers.Concurrency, [Finding(p1.CandidateId, "PERF001", "Services/OrderSummaryService.cs", 17, 17, "short", "strong")]));

        Assert.Equal(("a much longer mechanism text", "strong"), (p1.Mechanism, p1.Confidence));
        Assert.Equal(Consolidator.NotAnalyzed, p3.Mechanism);
    }

    [Fact]
    public async Task LlmOnlyFinding_IsValidatedAndGetsHostOwnedFields()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var absolute = Path.Combine(env.RepoPath, "Services", "OrderEvents.cs");

        var (result, _) = Run(env, [], (Reviewers.Memory, [Finding(null, "LLM-MEM-01", absolute, 18, 18, symbol: "Made.Up")]));

        var candidate = Assert.Single(result);
        Assert.Equal(("Services/OrderEvents.cs", "OrderAuditHandler.OrderAuditHandler", Categories.Memory, Origins.Specialist, EvidenceLevels.E0),
            (candidate.File, candidate.EnclosingSymbol, candidate.Category, candidate.Origin, candidate.Evidence));
        Assert.Equal(CandidateIds.Compute("LLM-MEM-01", "Services/OrderEvents.cs", "OrderAuditHandler.OrderAuditHandler", 18), candidate.CandidateId);
    }

    [Theory]
    [InlineData("Services/DoesNotExist.cs", 1, 1, "LLM-MEM-01", "does not exist")]
    [InlineData("../outside.cs", 1, 1, "LLM-MEM-01", "does not exist")]
    [InlineData("Services/OrderEvents.cs", 0, 1, "LLM-MEM-01", "not within the file")]
    [InlineData("Services/OrderEvents.cs", 5, 4, "LLM-MEM-01", "not within the file")]
    [InlineData("Services/OrderEvents.cs", 1, 999, "LLM-MEM-01", "not within the file")]
    [InlineData("Services/OrderEvents.cs", 5, 6, "CUSTOM-1", "rule ID must be")]
    public async Task InvalidLlmOnlyFinding_IsDiscardedWithAReason(string file, int start, int end, string ruleId, string reason)
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var (result, notes) = Run(env, [], (Reviewers.Memory, [Finding(null, ruleId, file, start, end)]));
        Assert.Empty(result);
        Assert.Contains(notes["MemoryReviewer"].Notes, n => n.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PerfRuleWithoutBaseline_IsRelabelled_AndOverlappingLlmFindingsMerge()
    {
        using var env = await new TestEnvironment().WithDemoRepoAsync();
        var (result, notes) = Run(env, [],
            (Reviewers.Concurrency, [Finding(null, "PERF003", "Services/NotificationService.cs", 13, 13, "fan-out")]),
            (Reviewers.Cpu, [Finding(null, "LLM-CONC-01", "Services/NotificationService.cs", 12, 14, "longer fan-out explanation")]));

        var candidate = Assert.Single(result);
        Assert.Equal(("LLM-CONC-00", "longer fan-out explanation"), (candidate.RuleId, candidate.Mechanism));
        Assert.Contains(notes["ConcurrencyReviewer"].Notes, n => n.Contains("only the analyzer can issue a PERF* rule ID", StringComparison.Ordinal));
        Assert.Equal(["ConcurrencyReviewer", "CpuReviewer"], candidate.Reviewers);
    }
}

public class CriticDecisionsTests
{
    private static Candidate Make(string ruleId, string symbol, int line, string evidence = "E1") => new()
    {
        CandidateId = CandidateIds.Compute(ruleId, "F.cs", symbol, line),
        Origin = Origins.Roslyn, RuleId = ruleId, File = "F.cs", StartLine = line, EndLine = line, EnclosingSymbol = symbol,
        Category = Categories.FromRuleId(ruleId)!, Confidence = Confidences.Candidate, Evidence = evidence,
    };

    private static CriticReply Reply(string decisions, string benchmarks = "[]")
    {
        ReviewerReplies.TryParseCritic($$"""{"reviewer":"VerificationReviewer","status":"completed","decisions":{{decisions}},"benchmarks":{{benchmarks}}}""",
            out var parsed, out var error);
        return parsed ?? throw new InvalidOperationException(error);
    }

    private static string DecisionsJson(params (string Id, string Decision, string Rationale)[] decisions) =>
        "[" + string.Join(",", decisions.Select(d => $$"""{"candidateId":"{{d.Id}}","decision":"{{d.Decision}}","rationale":"{{d.Rationale}}"}""")) + "]";

    private static string BenchmarksJson(params (string Id, string Kind, string Scenario)[] proposals) =>
        "[" + string.Join(",", proposals.Select(p => $$"""{"candidateId":"{{p.Id}}","benchmarkKind":"{{p.Kind}}","scenario":"{{p.Scenario}}"}""")) + "]";

    [Fact]
    public void KeepDowngradeRemove_Semantics()
    {
        var (a, b, c) = (Make("PERF001", "A.Run", 1), Make("PERF003", "B.Run", 2), Make("PERF004", "C.Run", 3));
        var outcome = CriticDecisions.Apply([a, b, c], Reply(DecisionsJson(
            (a.CandidateId, "keep", "real"), (b.CandidateId, "downgrade", "weak"), (c.CandidateId, "remove", "bounded"))));

        Assert.True(outcome.Complete);
        Assert.Equal(("keep", "candidate", "E1"), (a.CriticDecision, a.Confidence, a.Evidence));
        Assert.Equal(("downgrade", "low", "E1"), (b.CriticDecision, b.Confidence, b.Evidence)); // evidence never changes
        Assert.Equal(("remove", "bounded"), (c.CriticDecision, c.CriticRationale));
    }

    [Fact]
    public void MissingDuplicateAndInvalidDecisions_AreKeptAsNotReviewed_AndMakeTheCriticIncomplete()
    {
        var (a, b, c) = (Make("PERF001", "A.Run", 1), Make("PERF003", "B.Run", 2), Make("PERF004", "C.Run", 3));
        var reply = Reply(DecisionsJson(
            (a.CandidateId, "remove", "x"), (a.CandidateId, "keep", "y"), (b.CandidateId, "maybe", "z"), ("000000000000", "remove", "unknown")));

        var problems = CriticDecisions.Problems([a, b, c], reply);
        var outcome = CriticDecisions.Apply([a, b, c], reply);

        Assert.Contains(problems, p => p.StartsWith("duplicate decisions", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("invalid decision entry", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("unknown candidateId", StringComparison.Ordinal));
        Assert.Contains(problems, p => p == $"missing decision for {c.CandidateId}");
        Assert.False(outcome.Complete);
        foreach (var candidate in new[] { a, b, c })
        {
            Assert.Equal((Decisions.Keep, CriticDecisions.NotReviewed), (candidate.CriticDecision, candidate.CriticRationale));
        }

        Assert.Contains(outcome.Notes, n => n.Contains("unknown candidateId 000000000000", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownIdsAloneDoNotMakeTheCriticIncomplete()
    {
        var a = Make("PERF001", "A.Run", 1);
        var outcome = CriticDecisions.Apply([a], Reply(DecisionsJson((a.CandidateId, "keep", "real"), ("ffffffffffff", "keep", "?"))));
        Assert.True(outcome.Complete);
        Assert.Single(outcome.Notes);
    }

    [Fact]
    public void BenchmarkProposals_OnlyForKeptCandidatesAtTheTemplateSeam_AtMostTwo()
    {
        var seam = Make("PERF001", "OrderSummaryService.BuildSummariesAsync", 17);
        var otherPerf001 = Make("PERF001", "InvoiceService.BuildInvoiceLinesAsync", 19);
        var cache = Make("PERF004", "ReportCache.GetOrAdd", 28);
        var downgraded = Make("PERF003", "NotificationService.NotifyAllAsync", 13);
        var outcome = CriticDecisions.Apply([seam, otherPerf001, cache, downgraded], Reply(
            DecisionsJson(
                (seam.CandidateId, "keep", "r"), (otherPerf001.CandidateId, "keep", "r"),
                (cache.CandidateId, "keep", "r"), (downgraded.CandidateId, "downgrade", "r")),
            BenchmarksJson(
                (otherPerf001.CandidateId, "RepositoryCallAmplification", "OrderCustomerLookup"),
                (downgraded.CandidateId, "TaskFanOut", "NotificationRecipients"),
                (seam.CandidateId, "Invented", "X"),
                (seam.CandidateId, "RepositoryCallAmplification", "OrderCustomerLookup"),
                (cache.CandidateId, "CollectionGrowth", "ReportCache"),
                (cache.CandidateId, "CollectionGrowth", "ReportCache"))));

        Assert.Equal([seam.CandidateId, cache.CandidateId], outcome.AcceptedBenchmarks.Select(b => b.Proposal.CandidateId));
        Assert.Contains(outcome.Notes, n => n.Contains("is not the template's seam", StringComparison.Ordinal)); // P5 is not P1's seam
        Assert.Contains(outcome.Notes, n => n.Contains("only candidates the critic keeps", StringComparison.Ordinal));
        Assert.Contains(outcome.Notes, n => n.Contains("not in the template table", StringComparison.Ordinal));
        Assert.Contains(outcome.Notes, n => n.Contains("at most 2", StringComparison.Ordinal));
    }
}

public class TokenUsageAndStatusTests
{
    [Fact]
    public void UsageTotals_DistinguishNotReportedAndPartial()
    {
        var usage = TokenUsageReport.From(
        [
            ("MemoryReviewer", [new UsageRecord("m1", 100, 10, 1), new UsageRecord("m1", 50, null, 1)]),
            ("CpuReviewer", [new UsageRecord(null, null, null, null)]),
        ]);

        var memory = usage.Sessions[0];
        Assert.Equal((2, "150", "10, partial (1 of 2 calls reported)", "2 premium request cost units"),
            (memory.ModelCalls, memory.InputTokens.Display, memory.OutputTokens.Display, memory.Cost.Display));
        Assert.Equal(["m1"], memory.Models);

        var cpu = usage.Sessions[1];
        Assert.Equal(("not reported", "not reported", null), (cpu.InputTokens.Display, cpu.Cost.Display, cpu.Cost.Value));

        Assert.Equal((3, "150, partial (2 of 3 calls reported)", "2 premium request cost units, partial (2 of 3 calls reported)"),
            (usage.Total.ModelCalls, usage.Total.InputTokens.Display, usage.Total.Cost.Display));
    }

    [Fact]
    public void RunStatus_FailedBeatsPartial_PartialCombinesReasons()
    {
        Assert.Equal((RunStatuses.Complete, (string?)null), RunStatusRules.Compute(new RunFacts()));

        var partial = new RunFacts { SourceChanged = true, CriticProblem = "incomplete" };
        partial.FailedSpecialists.Add("CpuReviewer");
        Assert.Equal((RunStatuses.Partial, "source_changed; specialist_failed: CpuReviewer; critic_incomplete"), RunStatusRules.Compute(partial));

        partial.StaticAnalysisFailure = "build_failed";
        Assert.Equal((RunStatuses.Failed, "build_failed"), RunStatusRules.Compute(partial));
        Assert.Equal((RunStatuses.Failed, "timeout"), RunStatusRules.Compute(new RunFacts { StoppedEarly = "timeout", SourceChanged = true }));
    }

    [Fact]
    public void StrictHandler_PathContainment()
    {
        var repo = Path.Combine(Path.GetTempPath(), "repo");
        Assert.True(StrictPermissionHandler.IsInside(Path.Combine(repo, "Services", "A.cs"), repo));
        Assert.True(StrictPermissionHandler.IsInside(repo, repo));
        Assert.False(StrictPermissionHandler.IsInside(Path.Combine(Path.GetTempPath(), "repo-other", "A.cs"), repo));
        Assert.False(StrictPermissionHandler.IsInside(Path.Combine(repo, "..", "A.cs"), repo));
        Assert.False(StrictPermissionHandler.IsInside(null, repo));
    }
}
