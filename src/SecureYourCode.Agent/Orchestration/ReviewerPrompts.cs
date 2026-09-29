using System.Text.Json;
using SecureYourCode.Agent.Reporting;
using SecureYourCode.Agent.Verification;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>The task messages the host sends to each reviewer (the agent prompts live in Prompts/*.md).</summary>
public static class ReviewerPrompts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Specialist(ReviewerDefinition reviewer, string graphStatus, IReadOnlyList<Candidate> baseline)
    {
        var candidates = JsonSerializer.Serialize(baseline.Select(c => new
        {
            c.CandidateId, c.RuleId, c.File, c.StartLine, c.EndLine, c.EnclosingSymbol, c.Category, message = c.DiagnosticMessage,
        }), Json);

        return $$"""
            Review the repository for issues in your pillar ({{reviewer.Category}}).

            Graph status: {{graphStatus}}. {{GraphNote(graphStatus)}}

            Baseline candidates from the SecureYourCode Roslyn analyzer (each is a potential issue, identified by its candidateId):
            {{candidates}}

            What to do:
            1. For each baseline candidate in your pillar ({{reviewer.Category}}), read the code and enrich it: set "candidateId" to its ID, copy its ruleId, file, startLine, endLine and enclosingSymbol unchanged, and explain mechanism, trigger, graphPath (if you traced one with the graphify tools), confidence and fixDirection. Ignore candidates outside your pillar.
            2. Then look for other issues in your pillar that the analyzer did not report. Report each as a new finding without "candidateId", with ruleId "{{reviewer.LlmRulePrefix}}-01", "{{reviewer.LlmRulePrefix}}-02", and so on. Report each issue once; never report a baseline candidate's issue again as a new finding.
            3. Line numbers are 1-based and must point at the code that causes the issue. File paths are relative to the repository root, with '/' separators.

            Reply with only this JSON, with no prose and no code fences ("findings" is [] when there is nothing to report):
            {
              "reviewer": "{{reviewer.Name}}",
              "status": "completed",
              "findings": [
                {
                  "candidateId": "baseline ID when enriching a Roslyn candidate; omit for a new finding",
                  "ruleId": "the baseline ruleId, or {{reviewer.LlmRulePrefix}}-nn for a new finding",
                  "file": "relative/path.cs",
                  "startLine": 1,
                  "endLine": 1,
                  "enclosingSymbol": "Type.Member",
                  "mechanism": "what grows or repeats, and why",
                  "trigger": "what production input or traffic causes it",
                  "graphPath": "optional: execution path found via Graphify",
                  "confidence": "candidate | strong",
                  "fixDirection": "one sentence"
                }
              ]
            }
            """;
    }

    public static string Critic(string graphStatus, IReadOnlyList<Candidate> candidates)
    {
        var list = JsonSerializer.Serialize(candidates.Select(c => new
        {
            c.CandidateId, c.Origin, c.RuleId, c.File, c.StartLine, c.EndLine, c.EnclosingSymbol, c.Mechanism, c.Trigger, c.Confidence,
        }), Json);
        var templates = string.Join("\n", BenchmarkTemplates.All.Select(t => $"- \"benchmarkKind\": \"{t.Kind}\", \"scenario\": \"{t.Scenario}\""));

        return $$"""
            Challenge every candidate below and decide keep, downgrade or remove, with a one-sentence rationale. Give exactly one decision for every candidateId in the list, and no others.

            Graph status: {{graphStatus}}. {{GraphNote(graphStatus)}}

            Candidates:
            {{list}}

            You may also propose at most two verification benchmarks, only for candidates you keep, by copying one of these pairs exactly (any other value is ignored):
            {{templates}}

            Reply with only this JSON, with no prose and no code fences ("benchmarks" may be []):
            {
              "reviewer": "{{Reviewers.Critic.Name}}",
              "status": "completed",
              "decisions": [
                { "candidateId": "…", "decision": "keep | downgrade | remove", "rationale": "…" }
              ],
              "benchmarks": [
                { "candidateId": "…", "benchmarkKind": "…", "scenario": "…" }
              ]
            }
            """;
    }

    public static string Repair(string error) =>
        $"Your reply could not be used: {error} Reply again with only the JSON object in the required shape, with no prose and no code fences.";

    public static string CriticRepair(IEnumerable<string> problems, IEnumerable<string> candidateIds) =>
        $"Your reply could not be used: {string.Join("; ", problems)}. Give exactly one decision for each of these candidateIds " +
        $"and no others: {string.Join(", ", candidateIds)}. Reply again with only the JSON object in the required shape.";

    // The graph is preloaded; a project_path argument would point the server elsewhere and is denied by the strict handler.
    private const string GraphToolUsage =
        " The graph of this repository is already loaded: call the graphify tools without a project_path argument.";

    private static string GraphNote(string graphStatus) => graphStatus switch
    {
        GraphStatuses.Current => "The graphify tools query a code graph of the current source; use them to trace execution paths." + GraphToolUsage,
        GraphStatuses.Stale => "The graphify tools query a code graph that may not reflect the latest source; confirm what you find by reading the code." + GraphToolUsage,
        _ => "No code graph is available; use view, grep and glob.",
    };
}
