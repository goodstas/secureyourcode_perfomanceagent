using System.Globalization;
using System.Net;
using System.Text;
using SecureYourCode.Agent.Orchestration;

namespace SecureYourCode.Agent.Reporting;

/// <summary>
/// Renders report.html (plan §4.7): one self-contained offline page from the canonical report object. Every dynamic value
/// is HTML-encoded (text nodes and attribute values); only this renderer's hard-coded markup is trusted. Model output can
/// echo repository content, so nothing from the report is ever concatenated raw. The page's Content-Security-Policy also
/// forbids all scripts and external resources, as defence in depth.
/// </summary>
public static class HtmlReportRenderer
{
    public const string NoFixDirection = "no fix direction provided";

    private const string Css = """
        body{font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;margin:0;padding:24px;max-width:1100px;margin-inline:auto;color:#1f2937;background:#fff;line-height:1.45}
        h1{font-size:1.5rem;margin:0 0 4px}h2{font-size:1.2rem;margin:28px 0 8px;border-bottom:1px solid #e5e7eb;padding-bottom:4px}
        .muted{color:#6b7280}.banner{padding:10px 14px;border-radius:6px;margin:10px 0;font-weight:600}
        .banner.partial{background:#fef3c7;border:1px solid #f59e0b}.banner.failed{background:#fee2e2;border:1px solid #ef4444}
        .banner.graph{background:#e0e7ff;border:1px solid #6366f1}
        table{border-collapse:collapse;width:100%;margin:6px 0}th,td{border:1px solid #e5e7eb;padding:6px 8px;text-align:left;vertical-align:top}
        th{background:#f9fafb}.badge{display:inline-block;padding:1px 8px;border-radius:10px;font-size:.8rem;font-weight:600;color:#fff;margin-right:4px}
        .e0{background:#6b7280}.e1{background:#2563eb}.e2{background:#16a34a}.label{display:inline-block;padding:1px 8px;border-radius:10px;font-size:.8rem;border:1px solid #9ca3af;margin-right:4px}
        details.finding{border:1px solid #e5e7eb;border-radius:6px;margin:8px 0;padding:6px 10px}details.finding>summary{cursor:pointer}
        dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 12px;margin:8px 0}dt{font-weight:600;color:#374151}dd{margin:0;white-space:pre-wrap}
        code{background:#f3f4f6;padding:0 4px;border-radius:4px}
        """;

    public static string Render(Report report)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>SecureYourCode report ").Append(E(report.Run.RunId)).Append("</title>")
            .Append("<style>").Append(Css).Append("</style></head><body>");

        html.Append("<h1>SecureYourCode performance report</h1>")
            .Append("<div class=\"muted\">Run <code>").Append(E(report.Run.RunId)).Append("</code></div>");
        Banners(html, report);
        RunSection(html, report);
        TokenUsageSection(html, report.TokenUsage);
        ReviewersSection(html, report.Reviewers);

        var kept = report.Findings.Where(f => f.CriticDecision != Decisions.Downgrade).ToList();
        foreach (var category in new[] { Categories.Memory, Categories.Cpu, Categories.Concurrency })
        {
            FindingsSection(html, category, kept.Where(f => f.Category == category).ToList(), $"No findings in {category}.");
        }

        FindingsSection(html, "Low-confidence candidates", report.Findings.Where(f => f.CriticDecision == Decisions.Downgrade).ToList(),
            "No candidates were downgraded by the critic.");
        RejectedSection(html, report.RejectedCandidates);
        NotesSection(html, report.Notes);

        html.Append("</body></html>");
        return html.ToString();
    }

    private static void Banners(StringBuilder html, Report report)
    {
        if (report.Run.Status != RunStatuses.Complete)
        {
            html.Append("<div class=\"banner ").Append(report.Run.Status == RunStatuses.Failed ? "failed" : "partial").Append("\">Run status: ")
                .Append(E(report.Run.Status)).Append(report.Run.Reason is null ? "" : " — " + E(report.Run.Reason)).Append("</div>");
        }

        if (report.Run.GraphStatus != GraphStatuses.Current)
        {
            var meaning = report.Run.GraphStatus == GraphStatuses.Stale
                ? "reviewers used an older code graph that may not match the analyzed source"
                : "no code graph was available; reviewers ran without Graphify";
            html.Append("<div class=\"banner graph\">Graph status: ").Append(E(report.Run.GraphStatus)).Append(" — ").Append(meaning).Append("</div>");
        }
    }

    private static void RunSection(StringBuilder html, Report report)
    {
        html.Append("<h2>Run</h2><table>");
        Row(html, "Run status", report.Run.Status + (report.Run.Reason is null ? "" : " — " + report.Run.Reason));
        Row(html, "Graph status", report.Run.GraphStatus);
        Row(html, "Started", report.Run.StartedAt.ToString("u", CultureInfo.InvariantCulture));
        Row(html, "Finished", report.Run.FinishedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "not finished");
        Row(html, "Models used", report.Run.ModelsUsed.Count == 0 ? "none" : string.Join(", ", report.Run.ModelsUsed));
        html.Append("</table><h2>Provenance</h2><table>");
        Row(html, "LLM backend", report.Provenance.LlmBackend ?? "unknown");
        Row(html, "Commit", report.Provenance.CommitSha ?? "unknown");
        Row(html, "Dirty working tree", report.Provenance.Dirty is { } dirty ? (dirty ? "yes" : "no") : "unknown");
        Row(html, "Source fingerprint", report.Provenance.Fingerprint ?? "unknown");
        Row(html, "Graph built from fingerprint", report.Provenance.GraphFingerprint ?? "no graph");
        html.Append("</table>");
    }

    private static void TokenUsageSection(StringBuilder html, TokenUsageReport? usage)
    {
        html.Append("<h2>Token usage</h2>");
        if (usage is null)
        {
            html.Append("<p class=\"muted\">not reported</p>");
            return;
        }

        html.Append("<table><tr><th>Session</th><th>Model calls</th><th>Models</th><th>Input tokens</th><th>Output tokens</th>")
            .Append("<th>Cost (").Append(E(SessionUsage.CostUnit)).Append(")</th></tr>");
        foreach (var session in usage.Sessions.Append(usage.Total))
        {
            html.Append("<tr><td>").Append(E(session.Session)).Append("</td><td>").Append(session.ModelCalls)
                .Append("</td><td>").Append(E(session.Models.Count == 0 ? "not reported" : string.Join(", ", session.Models)))
                .Append("</td><td>").Append(E(session.InputTokens.Display)).Append("</td><td>").Append(E(session.OutputTokens.Display))
                .Append("</td><td>").Append(E(session.Cost.Display)).Append("</td></tr>");
        }

        html.Append("</table>");
    }

    private static void ReviewersSection(StringBuilder html, IReadOnlyList<ReviewerSummary> reviewers)
    {
        html.Append("<h2>Reviewers</h2><table><tr><th>Reviewer</th><th>Status</th><th>Graphify</th><th>Denied tool requests</th><th>Notes</th></tr>");
        foreach (var reviewer in reviewers)
        {
            html.Append("<tr><td>").Append(E(reviewer.Reviewer)).Append("</td><td>").Append(E(reviewer.Status))
                .Append(reviewer.Error is null ? "" : "<br><span class=\"muted\">" + E(reviewer.Error) + "</span>")
                .Append("</td><td>").Append(reviewer.GraphifyAvailable ? $"available, {reviewer.GraphifyCalls} calls" : "unavailable")
                .Append("</td><td>").Append(reviewer.DeniedToolRequests).Append("</td><td>")
                .Append(string.Join("<br>", reviewer.Notes.Select(E))).Append("</td></tr>");
        }

        html.Append("</table>");
    }

    private static void FindingsSection(StringBuilder html, string title, IReadOnlyList<Candidate> findings, string empty)
    {
        html.Append("<h2>").Append(E(title)).Append(" <span class=\"muted\">(").Append(findings.Count).Append(")</span></h2>");
        if (findings.Count == 0)
        {
            html.Append("<p class=\"muted\">").Append(E(empty)).Append("</p>");
            return;
        }

        foreach (var finding in findings)
        {
            Finding(html, finding);
        }
    }

    private static void RejectedSection(StringBuilder html, IReadOnlyList<Candidate> rejected)
    {
        html.Append("<h2>Rejected by critic <span class=\"muted\">(").Append(rejected.Count).Append(")</span></h2>");
        html.Append("<details><summary>Show rejected candidates</summary>");
        if (rejected.Count == 0)
        {
            html.Append("<p class=\"muted\">The critic rejected no candidates.</p>");
        }

        foreach (var candidate in rejected)
        {
            Finding(html, candidate);
        }

        html.Append("</details>");
    }

    private static void Finding(StringBuilder html, Candidate c)
    {
        var evidenceClass = c.Evidence switch { EvidenceLevels.E2 => "e2", EvidenceLevels.E1 => "e1", _ => "e0" };
        html.Append("<details class=\"finding\" id=\"candidate-").Append(E(c.CandidateId)).Append("\"><summary>")
            .Append("<span class=\"badge ").Append(evidenceClass).Append("\" title=\"").Append(E(EvidenceTitle(c))).Append("\">").Append(E(c.Evidence)).Append("</span>")
            .Append("<span class=\"label\">").Append(c.Origin == Origins.Roslyn ? "Roslyn" : "AI").Append("</span>")
            .Append("<span class=\"label\">confidence: ").Append(E(c.Confidence)).Append("</span> ")
            .Append("<code>").Append(E(c.RuleId)).Append("</code> ")
            .Append(E($"{c.File}:{c.StartLine}")).Append(c.EndLine != c.StartLine ? E($"-{c.EndLine}") : "")
            .Append(" — ").Append(E(c.EnclosingSymbol)).Append("</summary><dl>");

        Term(html, "Candidate ID", c.CandidateId);
        Term(html, "Category", c.Category);
        Term(html, "Origin", c.Origin == Origins.Roslyn ? "Roslyn analyzer" : "AI specialist" + (c.Reviewers.Count > 0 ? $" ({string.Join(", ", c.Reviewers)})" : ""));
        if (c.DiagnosticMessage is not null)
        {
            Term(html, "Analyzer message", c.DiagnosticMessage);
        }

        Term(html, "Mechanism", c.Mechanism ?? Consolidator.NotAnalyzed);
        Term(html, "Trigger", c.Trigger ?? "not provided");
        Term(html, "Execution path", c.GraphPath ?? "not found");
        Term(html, "Evidence", EvidenceTitle(c));
        Term(html, "Critic decision", $"{c.CriticDecision ?? "none"}: {c.CriticRationale ?? ""}");
        Term(html, "Verification", $"{c.Verification.Status}{(c.Verification.Template is null ? "" : " (" + c.Verification.Template + ")")}: {c.Verification.Reason}");
        if (c.Verification.Observations is { Count: > 0 } observations)
        {
            Term(html, "Benchmark observations", string.Join(", ", observations.Select(o => $"n={o.N}: {o.Value.ToString(CultureInfo.InvariantCulture)}")));
        }

        Term(html, "Fix direction", c.FixDirection ?? NoFixDirection);
        html.Append("</dl></details>");
    }

    private static string EvidenceTitle(Candidate c) => c.Evidence switch
    {
        EvidenceLevels.E2 => $"E2: verified by host benchmark template {c.Verification.Template}",
        EvidenceLevels.E1 => "E1: deterministic compiler evidence (Roslyn diagnostic)",
        _ => "E0: AI hypothesis",
    };

    private static void NotesSection(StringBuilder html, IReadOnlyList<string> notes)
    {
        html.Append("<h2>Notes</h2>");
        if (notes.Count == 0)
        {
            html.Append("<p class=\"muted\">No notes.</p>");
            return;
        }

        html.Append("<ul>").Append(string.Concat(notes.Select(n => "<li>" + E(n) + "</li>"))).Append("</ul>");
    }

    private static void Row(StringBuilder html, string label, string value) =>
        html.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");

    private static void Term(StringBuilder html, string label, string value) =>
        html.Append("<dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd>");

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}
