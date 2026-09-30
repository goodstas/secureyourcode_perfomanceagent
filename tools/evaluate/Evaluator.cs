using System.Text.Json;

namespace SecureYourCode.Evaluate;

/// <summary>One ground-truth case (test-assets/ground-truth.json).</summary>
public sealed record GroundTruthCase(string Id, bool ExpectedFinding, string? Category, string? RuleId, string File, int StartLine, int EndLine);

/// <summary>A final finding as the evaluator sees it (critic decision keep or downgrade).</summary>
public sealed record EvaluatedFinding(string CandidateId, string RuleId, string Category, string File, int StartLine, int EndLine);

public sealed record CaseMatch(string CaseId, string CandidateId);

/// <summary>metrics.json (plan §5). Precision and recall are numbers, or "undefined" when there is nothing to divide by.</summary>
public sealed record Metrics(
    string RunId,
    string RunStatus,
    bool CountsTowardTarget,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    object Precision,
    object Recall,
    bool TargetMet,
    IReadOnlyList<CaseMatch> Matches,
    IReadOnlyList<string> FalsePositiveFindings,
    IReadOnlyList<string> FalseNegativeCases,
    IReadOnlyList<string> Notes);

/// <summary>
/// Precision and recall against the ground truth, with the plan §5 evaluator semantics:
/// only final findings (critic decision keep or downgrade) count; rejected candidates and benchmark proposals are ignored;
/// findings and positive cases are matched one-to-one (same file, overlapping lines, same category) with a maximum matching;
/// a matched positive case is a true positive and an unmatched one a false negative; a final finding matching no positive case
/// is a false positive, in a negative-case region or anywhere else; negative cases never produce false negatives;
/// precision with zero final findings is "undefined" and never meets the target.
/// </summary>
public static class Evaluator
{
    public const double Target = 0.8;
    public const string Undefined = "undefined";

    public static IReadOnlyList<GroundTruthCase> ReadGroundTruth(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(c => new GroundTruthCase(
            c.GetProperty("id").GetString()!,
            c.GetProperty("expectedFinding").GetBoolean(),
            c.TryGetProperty("category", out var category) ? category.GetString() : null,
            c.TryGetProperty("ruleId", out var rule) ? rule.GetString() : null,
            c.GetProperty("file").GetString()!,
            c.GetProperty("startLine").GetInt32(),
            c.GetProperty("endLine").GetInt32()))];
    }

    /// <summary>Reads the run ID, the run status and the final findings (keep or downgrade) from a report.json.</summary>
    public static (string RunId, string RunStatus, IReadOnlyList<EvaluatedFinding> Findings) ReadReport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var run = root.GetProperty("run");
        var findings = root.GetProperty("findings").EnumerateArray()
            .Where(f => f.GetProperty("criticDecision").GetString() is "keep" or "downgrade")
            .Select(f => new EvaluatedFinding(
                f.GetProperty("candidateId").GetString()!,
                f.GetProperty("ruleId").GetString()!,
                f.GetProperty("category").GetString()!,
                f.GetProperty("file").GetString()!,
                f.GetProperty("startLine").GetInt32(),
                f.GetProperty("endLine").GetInt32()))
            .ToList();
        return (run.GetProperty("runId").GetString()!, run.GetProperty("status").GetString()!, findings);
    }

    public static Metrics Evaluate(string runId, string runStatus, IReadOnlyList<EvaluatedFinding> findings, IReadOnlyList<GroundTruthCase> cases)
    {
        var positives = cases.Where(c => c.ExpectedFinding).ToList();
        var findingOfCase = MaximumMatching(positives, findings);
        var matchedFindings = findingOfCase.Values.ToHashSet();

        var truePositives = findingOfCase.Count;
        var unmatchedFindings = Enumerable.Range(0, findings.Count).Where(i => !matchedFindings.Contains(i)).Select(i => findings[i]).ToList();

        var notes = new List<string>();
        foreach (var finding in unmatchedFindings)
        {
            var negative = cases.FirstOrDefault(c => !c.ExpectedFinding && Overlaps(c, finding));
            notes.Add(negative is null
                ? $"false positive {finding.CandidateId} ({finding.RuleId} {finding.File}:{finding.StartLine}) matches no positive case"
                : $"false positive {finding.CandidateId} ({finding.RuleId} {finding.File}:{finding.StartLine}) is in negative case {negative.Id}");
        }

        object precision = findings.Count == 0 ? Undefined : Math.Round((double)truePositives / findings.Count, 4);
        object recall = positives.Count == 0 ? Undefined : Math.Round((double)truePositives / positives.Count, 4);
        var countsTowardTarget = runStatus == "complete";
        var targetMet = countsTowardTarget && precision is double p && p >= Target && recall is double r && r >= Target;
        if (!countsTowardTarget)
        {
            notes.Add($"run status is {runStatus}: the metrics are recorded but do not count toward the target");
        }

        return new Metrics(
            runId,
            runStatus,
            countsTowardTarget,
            truePositives,
            unmatchedFindings.Count,
            positives.Count - truePositives,
            precision,
            recall,
            targetMet,
            [.. findingOfCase.Select(m => new CaseMatch(positives[m.Key].Id, findings[m.Value].CandidateId)).OrderBy(m => m.CaseId, StringComparer.Ordinal)],
            [.. unmatchedFindings.Select(f => f.CandidateId)],
            [.. positives.Where((_, i) => !findingOfCase.ContainsKey(i)).Select(c => c.Id)],
            notes);
    }

    /// <summary>The §5 match rule: same file, overlapping line range and the same category.</summary>
    public static bool Matches(GroundTruthCase groundTruth, EvaluatedFinding finding) =>
        Overlaps(groundTruth, finding) && groundTruth.Category == finding.Category;

    private static bool Overlaps(GroundTruthCase groundTruth, EvaluatedFinding finding) =>
        groundTruth.File == finding.File && finding.StartLine <= groundTruth.EndLine && groundTruth.StartLine <= finding.EndLine;

    /// <summary>Maximum bipartite matching (augmenting paths): positive-case index → finding index, each finding used at most once.</summary>
    private static Dictionary<int, int> MaximumMatching(IReadOnlyList<GroundTruthCase> positives, IReadOnlyList<EvaluatedFinding> findings)
    {
        var caseOfFinding = new Dictionary<int, int>();
        for (var c = 0; c < positives.Count; c++)
        {
            TryAssign(c, []);
        }

        return caseOfFinding.ToDictionary(kv => kv.Value, kv => kv.Key);

        bool TryAssign(int caseIndex, HashSet<int> visited)
        {
            for (var f = 0; f < findings.Count; f++)
            {
                if (!Matches(positives[caseIndex], findings[f]) || !visited.Add(f))
                {
                    continue;
                }

                if (!caseOfFinding.TryGetValue(f, out var other) || TryAssign(other, visited))
                {
                    caseOfFinding[f] = caseIndex;
                    return true;
                }
            }

            return false;
        }
    }
}
