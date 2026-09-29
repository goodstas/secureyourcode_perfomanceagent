using System.Text.Json;
using SecureYourCode.Agent.StaticAnalysis;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>Notes about one specialist's output that the report shows (identity mismatches, discarded findings).</summary>
public sealed class SpecialistDiagnostics
{
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Consolidation (plan §4.5, host code). Starts from the baseline candidates, which specialist output can enrich but never
/// remove or relabel; validates every LLM-only finding's location before it becomes a candidate.
/// </summary>
public sealed class Consolidator(string repoPath)
{
    public const string NotAnalyzed = "not analyzed by a specialist";

    public List<Candidate> Consolidate(
        IReadOnlyList<Candidate> baseline,
        IReadOnlyList<(ReviewerDefinition Reviewer, SpecialistReply Reply)> replies,
        IDictionary<string, SpecialistDiagnostics> diagnostics)
    {
        var llmOnly = new List<Candidate>();
        foreach (var (reviewer, reply) in replies)
        {
            var notes = diagnostics.TryGetValue(reviewer.Name, out var existing) ? existing : diagnostics[reviewer.Name] = new SpecialistDiagnostics();
            foreach (var finding in reply.Findings)
            {
                if (MatchBaseline(baseline, finding) is { } target)
                {
                    Enrich(target, finding, reviewer, notes);
                }
                else if (ValidateLlmOnly(finding, reviewer, notes) is { } candidate)
                {
                    MergeOrAdd(llmOnly, candidate, finding, reviewer);
                }
            }
        }

        foreach (var candidate in baseline.Where(c => c.Mechanism is null))
        {
            candidate.Mechanism = NotAnalyzed;
        }

        return [.. baseline, .. llmOnly];
    }

    /// <summary>A finding enriches a baseline candidate by candidateId, or else by same ruleId + file with a range containing its startLine.</summary>
    private Candidate? MatchBaseline(IReadOnlyList<Candidate> baseline, SpecialistFinding finding)
    {
        if (finding.CandidateId is { } id && baseline.FirstOrDefault(c => c.CandidateId == id) is { } byId)
        {
            return byId;
        }

        var file = TryNormalize(finding.File);
        return baseline.FirstOrDefault(c =>
            c.RuleId == finding.RuleId && c.File == file && finding.StartLine <= c.StartLine && c.StartLine <= finding.EndLine);
    }

    private static void Enrich(Candidate target, SpecialistFinding finding, ReviewerDefinition reviewer, SpecialistDiagnostics notes)
    {
        // Host-owned identity: never relabel, only note a disagreement.
        if (finding.RuleId != target.RuleId || finding.StartLine != target.StartLine || finding.EndLine != target.EndLine
            || (finding.EnclosingSymbol is { } symbol && !SymbolMatches(symbol, target.EnclosingSymbol))
            || !finding.File.Replace('\\', '/').EndsWith(target.File, StringComparison.OrdinalIgnoreCase))
        {
            notes.Notes.Add($"specialist_identity_mismatch: {target.CandidateId} (specialist values ignored)");
        }

        ApplyAnalysis(target, finding, reviewer);
    }

    /// <summary>Fills only the enrichable fields: the longer text per field and the higher confidence win.</summary>
    private static void ApplyAnalysis(Candidate target, SpecialistFinding finding, ReviewerDefinition reviewer)
    {
        target.Mechanism = Longer(target.Mechanism, finding.Mechanism);
        target.Trigger = Longer(target.Trigger, finding.Trigger);
        target.GraphPath = Longer(target.GraphPath, finding.GraphPath);
        target.FixDirection = Longer(target.FixDirection, finding.FixDirection);
        if (Confidences.Rank(finding.Confidence) > Confidences.Rank(target.Confidence))
        {
            target.Confidence = finding.Confidence;
        }

        if (!target.Reviewers.Contains(reviewer.Name))
        {
            target.Reviewers.Add(reviewer.Name);
        }
    }

    /// <summary>The six location checks of plan §4.5 step 2; a failure discards the finding with a recorded reason.</summary>
    private Candidate? ValidateLlmOnly(SpecialistFinding finding, ReviewerDefinition reviewer, SpecialistDiagnostics notes)
    {
        var label = $"{finding.RuleId} at {finding.File}:{finding.StartLine}";
        var file = TryNormalize(finding.File);
        var full = file is null ? null : Path.Combine(repoPath, file);
        if (file is null || !File.Exists(full))
        {
            notes.Notes.Add($"discarded {label}: file does not exist inside the repository");
            return null;
        }

        var lineCount = File.ReadAllLines(full).Length;
        if (finding.StartLine < 1 || finding.EndLine < finding.StartLine || finding.EndLine > lineCount)
        {
            notes.Notes.Add($"discarded {label}: lines {finding.StartLine}-{finding.EndLine} are not within the file (1-{lineCount})");
            return null;
        }

        var enclosingSymbol = EnclosingSymbolResolver.Resolve(File.ReadAllText(full), finding.StartLine);
        var ruleId = finding.RuleId;
        if (ruleId.StartsWith("PERF", StringComparison.Ordinal))
        {
            var prefix = Categories.FromRuleId(ruleId) is { } category ? Reviewers.PillarPrefix(category) : reviewer.LlmRulePrefix;
            ruleId = $"{prefix ?? reviewer.LlmRulePrefix}-00";
            notes.Notes.Add($"relabelled {label} as {ruleId}: only the analyzer can issue a PERF* rule ID");
        }

        if (!ruleId.StartsWith("LLM-", StringComparison.Ordinal) || Categories.FromRuleId(ruleId) is not { } mapped)
        {
            notes.Notes.Add($"discarded {label}: rule ID must be LLM-MEM-nn, LLM-CPU-nn or LLM-CONC-nn");
            return null;
        }

        var candidate = new Candidate
        {
            CandidateId = CandidateIds.Compute(ruleId, file, enclosingSymbol, finding.StartLine),
            Origin = Origins.Specialist,
            RuleId = ruleId,
            File = file,
            StartLine = finding.StartLine,
            EndLine = finding.EndLine,
            EnclosingSymbol = enclosingSymbol,
            Category = mapped,
            Confidence = finding.Confidence,
            Evidence = EvidenceLevels.E0,
            Mechanism = finding.Mechanism,
            Trigger = finding.Trigger,
            GraphPath = finding.GraphPath,
            FixDirection = finding.FixDirection,
        };
        candidate.Reviewers.Add(reviewer.Name);
        return candidate;
    }

    /// <summary>Two validated LLM-only findings with the same file, enclosing symbol and category and overlapping lines are merged.</summary>
    private static void MergeOrAdd(List<Candidate> llmOnly, Candidate candidate, SpecialistFinding finding, ReviewerDefinition reviewer)
    {
        var existing = llmOnly.FirstOrDefault(c => c.File == candidate.File && c.EnclosingSymbol == candidate.EnclosingSymbol
            && c.Category == candidate.Category && c.StartLine <= candidate.EndLine && candidate.StartLine <= c.EndLine);
        if (existing is null)
        {
            llmOnly.Add(candidate);
            return;
        }

        ApplyAnalysis(existing, finding, reviewer);
    }

    private string? TryNormalize(string file)
    {
        try
        {
            return SarifParser.NormalizeSarifPath(file, null, default(JsonElement), repoPath, repoPath);
        }
        catch (Exception exception) when (exception is SarifDataException or ArgumentException or UriFormatException or IOException)
        {
            return null;
        }
    }

    private static bool SymbolMatches(string reported, string host) =>
        reported == host || reported.EndsWith("." + host, StringComparison.Ordinal);

    private static string? Longer(string? current, string? candidate) =>
        string.IsNullOrWhiteSpace(candidate) ? current : (current is null || candidate.Length > current.Length ? candidate : current);
}
