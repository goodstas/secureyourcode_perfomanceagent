using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.Reporting;

namespace SecureYourCode.Agent.Tests;

public class ReportingTests
{
    private const string Script = "<script>alert('x')</script>";
    private const string AttributeBreak = "\"><img src=x onerror=alert(1)>";

    private static Candidate Finding(string rule, string file, int line, string symbol, string decision, string evidence = "E1", string origin = Origins.Roslyn) => new()
    {
        CandidateId = CandidateIds.Compute(rule, file, symbol, line),
        Origin = origin,
        RuleId = rule,
        File = file,
        StartLine = line,
        EndLine = line,
        EnclosingSymbol = symbol,
        Category = Categories.FromRuleId(rule)!,
        Confidence = Confidences.Candidate,
        Evidence = evidence,
        DiagnosticMessage = origin == Origins.Roslyn ? "analyzer message" : null,
        Mechanism = "mechanism",
        Trigger = "trigger",
        FixDirection = "fix",
        CriticDecision = decision,
        CriticRationale = "rationale",
    };

    private static Report Fixture(string status = RunStatuses.Partial, string graph = GraphStatuses.Stale, string runId = "20260929-120000-abcd")
    {
        var report = new Report { Run = new RunInfo { RunId = runId, StartedAt = DateTimeOffset.UnixEpoch, Status = status, Reason = status == RunStatuses.Complete ? null : "critic_incomplete", GraphStatus = graph } };
        report.Provenance.CommitSha = "0123456789abcdef";
        report.Provenance.LlmBackend = "Copilot (model auto)";
        report.Provenance.Dirty = false;
        report.Provenance.Fingerprint = "fp";
        var p1 = Finding("PERF001", "Services/OrderSummaryService.cs", 17, "OrderSummaryService.BuildSummariesAsync", Decisions.Keep, "E2");
        p1.Verification = new VerificationResult("verified", "RepositoryCallAmplification/OrderCustomerLookup", "verified by host benchmark template RepositoryCallAmplification/OrderCustomerLookup",
            [new BenchmarkObservation(10, 10), new BenchmarkObservation(100, 100), new BenchmarkObservation(1000, 1000)], "E1");
        var p3 = Finding("PERF004", "Services/ReportCache.cs", 28, "ReportCache.GetOrAdd", Decisions.Keep);
        p3.FixDirection = null;
        var p4 = Finding("LLM-MEM-01", "Services/OrderEvents.cs", 18, "OrderAuditHandler.OrderAuditHandler", Decisions.Keep, "E0", Origins.Specialist);
        var p2 = Finding("PERF003", "Services/NotificationService.cs", 13, "NotificationService.NotifyAllAsync", Decisions.Keep);
        var low = Finding("PERF001", "Services/InvoiceService.cs", 19, "InvoiceService.BuildInvoiceLinesAsync", Decisions.Downgrade);
        low.Confidence = Confidences.Low;
        var rejected = Finding("LLM-CPU-01", "Services/OrderEvents.cs", 17, "OrderAuditHandler.OrderAuditHandler", Decisions.Remove, "E0", Origins.Specialist);
        report.Findings.AddRange([p1, p3, p4, p2, low]);
        report.RejectedCandidates.Add(rejected);
        report.Reviewers.Add(new ReviewerSummary { Reviewer = "MemoryReviewer", Status = "completed", GraphifyAvailable = true, GraphifyCalls = 3 });
        report.TokenUsage = TokenUsageReport.From([("MemoryReviewer", [new UsageRecord("m", 100, 10, 1)])]);
        report.Notes.Add("a note");
        return report;
    }

    [Fact]
    public void HtmlShowsEverySectionBadgeLabelAndBanner()
    {
        var html = HtmlReportRenderer.Render(Fixture());

        foreach (var section in new[] { ">Run<", ">Provenance<", ">Token usage<", ">Reviewers<", "Memory &amp; Allocation", "CPU &amp; Amplification",
                     ">Concurrency <", "Low-confidence candidates", "Rejected by critic", ">Notes<" })
        {
            Assert.Contains(section, html);
        }

        Assert.Contains("class=\"banner partial\">Run status: partial — critic_incomplete", html);
        Assert.Contains("class=\"banner graph\">Graph status: stale", html);
        Assert.Contains("class=\"badge e2\"", html);
        Assert.Contains("class=\"badge e1\"", html);
        Assert.Contains("class=\"badge e0\"", html);
        Assert.Contains("<span class=\"label\">Roslyn</span>", html);
        Assert.Contains("<span class=\"label\">AI</span>", html);
        Assert.Contains("confidence: low", html);
        Assert.Contains("E2: verified by host benchmark template RepositoryCallAmplification/OrderCustomerLookup", html);
        Assert.Contains(HtmlReportRenderer.NoFixDirection, html);
        Assert.Contains("premium request cost units", html);
        Assert.Contains("0123456789abcdef", html);
        Assert.Matches("<h2>Rejected by critic <span class=\"muted\">\\(1\\)</span></h2><details><summary>", html); // collapsed by default
        Assert.DoesNotContain("<details open", html);
        Assert.Equal(Regex.Matches(html, "<details").Count, Regex.Matches(html, "</details>").Count);
        Assert.DoesNotContain("severity", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompleteRunWithCurrentGraph_HasNoBanner()
    {
        var html = HtmlReportRenderer.Render(Fixture(RunStatuses.Complete, GraphStatuses.Current));
        Assert.DoesNotContain("class=\"banner", html);
    }

    [Fact]
    public void FailedRun_HasAFailedBanner()
    {
        var report = Fixture(RunStatuses.Failed, GraphStatuses.None);
        report.Run.Reason = "timeout";
        var html = HtmlReportRenderer.Render(report);
        Assert.Contains("class=\"banner failed\">Run status: failed — timeout", html);
        Assert.Contains("Graph status: none", html);
    }

    [Fact]
    public void EveryDynamicStringIsHtmlEncoded_InTextAndAttributes()
    {
        // A <script> copied from a source comment can reach any model-written or repository-derived field.
        var report = Fixture();
        report.Run.Reason = Script;
        report.Run.ModelsUsed.Add(Script);
        report.Provenance.CommitSha = Script;
        report.Provenance.LlmBackend = Script;
        report.Notes.Add(Script);
        report.Reviewers[0].Notes.Add(Script);
        report.Reviewers[0].Error = AttributeBreak;
        var evil = new Candidate
        {
            CandidateId = AttributeBreak, // lands in an id attribute
            Origin = Origins.Specialist, RuleId = Script, File = Script, StartLine = 1, EndLine = 2, EnclosingSymbol = Script,
            Category = Categories.Memory, Confidence = Script, Evidence = "E0", DiagnosticMessage = Script,
            Mechanism = Script, Trigger = AttributeBreak, GraphPath = Script, FixDirection = Script,
            CriticDecision = Decisions.Keep, CriticRationale = Script,
            Verification = new VerificationResult(Script, Script, Script),
        };
        evil.Reviewers.Add(Script);
        report.Findings.Add(evil);
        report.RejectedCandidates.Add(evil);
        report.TokenUsage = TokenUsageReport.From([(Script, [new UsageRecord(Script, 1, 1, 1)])]);

        var html = HtmlReportRenderer.Render(report);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"><img", html); // no attribute breakout
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html);
        Assert.Contains("id=\"candidate-&quot;&gt;&lt;img src=x onerror=alert(1)&gt;\"", html);
        Assert.Contains("content=\"default-src 'none'; style-src 'unsafe-inline'\"", html); // scripts blocked even if encoding failed
    }

    [Fact]
    public async Task PublishesBothFilesTransactionally_AndUpdatesLatest()
    {
        using var env = new TestEnvironment();
        var publisher = new FileReportPublisher(env.Paths, NullLogger<FileReportPublisher>.Instance);
        var report = Fixture();
        report.Provenance.Dirty = true;

        await publisher.PublishAsync(report, "run", CancellationToken.None);

        var folder = Path.Combine(env.Paths.ReportsDirectory, "20260929-120000-abcd_0123456-dirty");
        Assert.True(File.Exists(Path.Combine(folder, "report.json")));
        Assert.True(File.Exists(Path.Combine(folder, "report.html")));
        Assert.Equal("20260929-120000-abcd_0123456-dirty", await File.ReadAllTextAsync(Path.Combine(env.Paths.ReportsDirectory, "latest.txt")));
        Assert.Empty(Directory.GetDirectories(env.Paths.ReportsDirectory, "*.tmp"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "report.json")));
        Assert.Equal("20260929-120000-abcd", json.RootElement.GetProperty("run").GetProperty("runId").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("findings").GetArrayLength());
        Assert.Equal(ReportJson.Serialize(report), await File.ReadAllTextAsync(Path.Combine(folder, "report.json")));
    }

    [Fact]
    public async Task FailedPublication_LeavesNoTempFolder_AndLatestUnchanged()
    {
        using var env = new TestEnvironment();
        var publisher = new FileReportPublisher(env.Paths, NullLogger<FileReportPublisher>.Instance);
        var first = Fixture();
        await publisher.PublishAsync(first, "run", CancellationToken.None);
        var latest = Path.Combine(env.Paths.ReportsDirectory, "latest.txt");
        var before = await File.ReadAllTextAsync(latest);

        var second = Fixture(runId: "20260929-130000-beef");
        await File.WriteAllTextAsync(Path.Combine(env.Paths.ReportsDirectory, FileReportPublisher.FolderName(second)), "a file where the folder must go");

        var error = await Assert.ThrowsAsync<ReportPublicationException>(() => publisher.PublishAsync(second, "run", CancellationToken.None));

        Assert.Same(second, error.Report);
        Assert.Equal(before, await File.ReadAllTextAsync(latest));
        Assert.Empty(Directory.GetDirectories(env.Paths.ReportsDirectory, "*.tmp"));
    }
}
