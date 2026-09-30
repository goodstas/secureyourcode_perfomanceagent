using System.Text.Json;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.Reporting;
using SecureYourCode.Evaluate;

namespace SecureYourCode.Agent.Tests;

public class EvaluatorTests
{
    private const string Cpu = Categories.Cpu;
    private const string Memory = Categories.Memory;

    private static readonly IReadOnlyList<GroundTruthCase> GroundTruth = Evaluator.ReadGroundTruth(File.ReadAllText(GroundTruthPath()));

    private static string GroundTruthPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SecureYourCode.slnx")))
            {
                return Path.Combine(directory.FullName, "test-assets", "ground-truth.json");
            }
        }

        throw new InvalidOperationException("SecureYourCode.slnx not found");
    }

    private static Candidate Final(string rule, string file, int line, string decision = Decisions.Keep, string? category = null) => new()
    {
        CandidateId = CandidateIds.Compute(rule, file, "Symbol", line),
        Origin = rule.StartsWith("PERF", StringComparison.Ordinal) ? Origins.Roslyn : Origins.Specialist,
        RuleId = rule,
        File = file,
        StartLine = line,
        EndLine = line,
        EnclosingSymbol = "Symbol",
        Category = category ?? Categories.FromRuleId(rule)!,
        Confidence = decision == Decisions.Downgrade ? Confidences.Low : Confidences.Candidate,
        Evidence = rule.StartsWith("PERF", StringComparison.Ordinal) ? EvidenceLevels.E1 : EvidenceLevels.E0,
        CriticDecision = decision,
        CriticRationale = "rationale",
    };

    // The five final findings of the recorded live runs (P1–P5 at their diagnostic lines).
    private static List<Candidate> ExactlyP1ToP5() =>
    [
        Final("PERF001", "Services/OrderSummaryService.cs", 17),
        Final("PERF003", "Services/NotificationService.cs", 13),
        Final("PERF004", "Services/ReportCache.cs", 28),
        Final("LLM-MEM-01", "Services/OrderEvents.cs", 18),
        Final("PERF001", "Services/InvoiceService.cs", 19, Decisions.Downgrade),
    ];

    /// <summary>Round-trips through the host's own report.json serialization, so the evaluator reads exactly what the host writes.</summary>
    private static Metrics EvaluateReport(IEnumerable<Candidate> findings, IEnumerable<Candidate>? rejected = null, string status = RunStatuses.Complete)
    {
        var report = new Report { Run = new RunInfo { RunId = "20260930-000000-test", StartedAt = DateTimeOffset.UnixEpoch, Status = status } };
        report.Findings.AddRange(findings);
        report.RejectedCandidates.AddRange(rejected ?? []);
        var (runId, runStatus, final) = Evaluator.ReadReport(ReportJson.Serialize(report));
        return Evaluator.Evaluate(runId, runStatus, final, GroundTruth);
    }

    [Fact]
    public void GroundTruth_IsStillTheH0File()
    {
        // Written in H0 and never changed after observing results (plan §5). A change here is a change to the target itself.
        string[] expected =
        [
            "P1 True CPU & Amplification PERF001 Services/OrderSummaryService.cs 12-22",
            "P2 True Concurrency PERF003 Services/NotificationService.cs 11-14",
            "P3 True Memory & Allocation PERF004 Services/ReportCache.cs 7-33",
            "P4 True Memory & Allocation LLM-MEM Services/OrderEvents.cs 5-24",
            "P5 True CPU & Amplification PERF001 Services/InvoiceService.cs 12-25",
            "N1 False   Services/BatchedOrderService.cs 12-25",
            "N2 False   Services/BatchedNotificationService.cs 11-17",
            "N3 False   Services/ProductCache.cs 7-27",
            "N4 False   Services/InventoryEvents.cs 3-26",
        ];
        Assert.Equal(expected, GroundTruth.Select(c => $"{c.Id} {c.ExpectedFinding} {c.Category} {c.RuleId} {c.File} {c.StartLine}-{c.EndLine}"));
    }

    [Fact]
    public void ExactlyP1ToP5_MeetsTheTarget_AndNegativesNeverProduceFalseNegatives()
    {
        var metrics = EvaluateReport(ExactlyP1ToP5());

        Assert.Equal((5, 0, 0), (metrics.TruePositives, metrics.FalsePositives, metrics.FalseNegatives));
        Assert.Equal(1.0, metrics.Precision);
        Assert.Equal(1.0, metrics.Recall);
        Assert.True(metrics.CountsTowardTarget);
        Assert.True(metrics.TargetMet);
        Assert.Equal(["P1", "P2", "P3", "P4", "P5"], metrics.Matches.Select(m => m.CaseId));
        Assert.Empty(metrics.Notes);
    }

    [Fact]
    public void OnlyFinalFindingsCount_RejectedCandidatesAreIgnored()
    {
        // Rejected candidates sit on P2 and on the negative case N1; neither may change the metrics.
        var findings = ExactlyP1ToP5().Where(f => f.RuleId != "PERF003").ToList();
        var rejected = new[]
        {
            Final("PERF003", "Services/NotificationService.cs", 13, Decisions.Remove),
            Final("LLM-CPU-01", "Services/BatchedOrderService.cs", 18, Decisions.Remove),
        };

        var metrics = EvaluateReport(findings, rejected);

        Assert.Equal((4, 0, 1), (metrics.TruePositives, metrics.FalsePositives, metrics.FalseNegatives));
        Assert.Equal(["P2"], metrics.FalseNegativeCases);
        Assert.Equal(1.0, metrics.Precision);
        Assert.Equal(0.8, metrics.Recall);
        Assert.True(metrics.TargetMet); // ≥ 0.8 is inclusive
    }

    [Fact]
    public void AFindingWithTheWrongCategory_OrASecondFindingOnTheSameCase_IsAFalsePositive()
    {
        // The H4 run-1 shape: the CPU reviewer restated P4 (a Memory case) as a CPU finding.
        var findings = ExactlyP1ToP5();
        findings.Add(Final("LLM-CPU-01", "Services/OrderEvents.cs", 17));
        findings.Add(Final("LLM-MEM-02", "Services/OrderEvents.cs", 20));

        var metrics = EvaluateReport(findings);

        Assert.Equal((5, 2, 0), (metrics.TruePositives, metrics.FalsePositives, metrics.FalseNegatives));
        Assert.Equal(0.7143, metrics.Precision);
        Assert.False(metrics.TargetMet);
        Assert.All(metrics.Notes, n => Assert.Contains("matches no positive case", n));
    }

    [Fact]
    public void AFindingInANegativeCase_IsAFalsePositive_AndSaysWhichCase()
    {
        var findings = ExactlyP1ToP5();
        var n1 = Final("PERF001", "Services/BatchedOrderService.cs", 18);
        findings.Add(n1);

        var metrics = EvaluateReport(findings);

        Assert.Equal((5, 1, 0), (metrics.TruePositives, metrics.FalsePositives, metrics.FalseNegatives));
        Assert.Equal([n1.CandidateId], metrics.FalsePositiveFindings);
        Assert.Contains(metrics.Notes, n => n.EndsWith("is in negative case N1", StringComparison.Ordinal));
        Assert.Equal(0.8333, metrics.Precision);
    }

    [Fact]
    public void MatchingIsOneToOne_AndMaximal()
    {
        // Case A: lines 1-10, case B: lines 5-15. Finding at 7 fits both, finding at 3 fits only A.
        // A greedy pass that gives A the first finding would leave B unmatched; the maximum matching finds both.
        GroundTruthCase[] cases =
        [
            new("A", true, Cpu, "PERF001", "X.cs", 1, 10),
            new("B", true, Cpu, "PERF001", "X.cs", 5, 15),
        ];
        EvaluatedFinding[] findings = [new("f7", "PERF001", Cpu, "X.cs", 7, 7), new("f3", "PERF001", Cpu, "X.cs", 3, 3)];

        var both = Evaluator.Evaluate("r", RunStatuses.Complete, findings, cases);
        Assert.Equal(["A=f3", "B=f7"], both.Matches.Select(m => $"{m.CaseId}={m.CandidateId}"));

        // One finding can never satisfy two cases.
        var single = Evaluator.Evaluate("r", RunStatuses.Complete, [findings[0]], cases);
        Assert.Equal((1, 0, 1), (single.TruePositives, single.FalsePositives, single.FalseNegatives));
    }

    [Fact]
    public void MatchNeedsTheSameFile_OverlappingLines_AndTheSameCategory()
    {
        var p3 = GroundTruth.Single(c => c.Id == "P3");
        Assert.True(Evaluator.Matches(p3, new("x", "PERF004", Memory, "Services/ReportCache.cs", 33, 40)));  // touches the last line
        Assert.False(Evaluator.Matches(p3, new("x", "PERF004", Memory, "Services/ReportCache.cs", 34, 40)));
        Assert.False(Evaluator.Matches(p3, new("x", "PERF004", Cpu, "Services/ReportCache.cs", 28, 28)));
        Assert.False(Evaluator.Matches(p3, new("x", "PERF004", Memory, "Services/ProductCache.cs", 28, 28)));
    }

    [Fact]
    public void ZeroFindings_HasUndefinedPrecision_AndNeverMeetsTheTarget()
    {
        var metrics = EvaluateReport([]);

        Assert.Equal(Evaluator.Undefined, metrics.Precision);
        Assert.Equal(0.0, metrics.Recall);
        Assert.Equal(5, metrics.FalseNegatives);
        Assert.False(metrics.TargetMet);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(metrics, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("undefined", json.RootElement.GetProperty("precision").GetString());
        Assert.Equal(0.0, json.RootElement.GetProperty("recall").GetDouble());
    }

    [Theory]
    [InlineData(RunStatuses.Partial)]
    [InlineData(RunStatuses.Failed)]
    public void NonCompleteRuns_AreRecordedButDoNotCount(string status)
    {
        var metrics = EvaluateReport(ExactlyP1ToP5(), status: status);

        Assert.Equal(5, metrics.TruePositives);
        Assert.Equal(1.0, metrics.Precision);
        Assert.False(metrics.CountsTowardTarget);
        Assert.False(metrics.TargetMet);
        Assert.Contains(metrics.Notes, n => n.Contains($"run status is {status}", StringComparison.Ordinal));
    }
}
