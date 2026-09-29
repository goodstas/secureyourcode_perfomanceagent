using System.Text.Json;
using SecureYourCode.Agent.Verification;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>The critic's validated decisions and the notes the report shows about them.</summary>
public sealed class CriticOutcome
{
    public List<string> Notes { get; } = [];

    public List<(BenchmarkProposal Proposal, BenchmarkTemplate Template)> AcceptedBenchmarks { get; } = [];

    /// <summary>False when decisions were missing, invalid or duplicated after the repair attempt.</summary>
    public bool Complete { get; set; } = true;
}

/// <summary>Validation and application of the critic's decisions and benchmark proposals (plan §4.5).</summary>
public static class CriticDecisions
{
    public const string NotReviewed = "not reviewed by critic";
    public const int MaxBenchmarkProposals = 2;

    /// <summary>Problems that justify the single repair request: missing, duplicate or invalid decisions, unknown IDs.</summary>
    public static List<string> Problems(IReadOnlyList<Candidate> candidates, CriticReply reply)
    {
        var problems = new List<string>();
        var ids = candidates.Select(c => c.CandidateId).ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in reply.Decisions)
        {
            if (Read(raw) is not { } decision)
            {
                problems.Add($"invalid decision entry: {Truncate(raw.GetRawText())}");
                continue;
            }

            if (!ids.Contains(decision.CandidateId))
            {
                problems.Add($"unknown candidateId {decision.CandidateId}");
                continue;
            }

            seen[decision.CandidateId] = seen.GetValueOrDefault(decision.CandidateId) + 1;
        }

        problems.AddRange(seen.Where(s => s.Value > 1).Select(s => $"duplicate decisions for {s.Key}"));
        problems.AddRange(ids.Where(id => !seen.ContainsKey(id)).Select(id => $"missing decision for {id}"));
        return problems;
    }

    /// <summary>
    /// Applies decisions: exactly one valid decision per candidate is used. Missing, invalid or duplicated decisions leave the
    /// candidate as keep with "not reviewed by critic" (a duplicate is never resolved by picking one), and make the critic
    /// incomplete. Decisions for unknown IDs are ignored and noted. Downgrade sets confidence low and never changes evidence.
    /// </summary>
    public static CriticOutcome Apply(IReadOnlyList<Candidate> candidates, CriticReply? reply)
    {
        var outcome = new CriticOutcome();
        var byId = candidates.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var valid = new Dictionary<string, List<CriticDecision>>(StringComparer.Ordinal);
        foreach (var raw in reply?.Decisions ?? [])
        {
            if (Read(raw) is not { } decision)
            {
                outcome.Notes.Add($"ignored invalid critic decision: {Truncate(raw.GetRawText())}");
                continue;
            }

            if (!byId.ContainsKey(decision.CandidateId))
            {
                outcome.Notes.Add($"ignored critic decision for unknown candidateId {decision.CandidateId}");
                continue;
            }

            (valid.TryGetValue(decision.CandidateId, out var list) ? list : valid[decision.CandidateId] = []).Add(decision);
        }

        foreach (var candidate in candidates)
        {
            if (!valid.TryGetValue(candidate.CandidateId, out var decisions) || decisions.Count != 1)
            {
                outcome.Complete = false;
                candidate.CriticDecision = Decisions.Keep;
                candidate.CriticRationale = NotReviewed;
                if (decisions is { Count: > 1 })
                {
                    outcome.Notes.Add($"critic returned {decisions.Count} decisions for {candidate.CandidateId}; treated as not reviewed");
                }

                continue;
            }

            var chosen = decisions[0];
            candidate.CriticDecision = chosen.Decision;
            candidate.CriticRationale = chosen.Rationale;
            if (chosen.Decision == Decisions.Downgrade)
            {
                candidate.Confidence = Confidences.Low;
            }
        }

        AcceptBenchmarks(byId, reply?.Benchmarks ?? [], outcome);
        return outcome;
    }

    private static void AcceptBenchmarks(Dictionary<string, Candidate> byId, IReadOnlyList<BenchmarkProposal> proposals, CriticOutcome outcome)
    {
        foreach (var proposal in proposals)
        {
            var label = $"{proposal.BenchmarkKind}/{proposal.Scenario} for {proposal.CandidateId}";
            if (outcome.AcceptedBenchmarks.Count >= MaxBenchmarkProposals)
            {
                outcome.Notes.Add($"ignored benchmark proposal {label}: at most {MaxBenchmarkProposals} proposals");
            }
            else if (!byId.TryGetValue(proposal.CandidateId, out var candidate) || candidate.CriticDecision != Decisions.Keep
                     || candidate.CriticRationale == NotReviewed)
            {
                outcome.Notes.Add($"ignored benchmark proposal {label}: only candidates the critic keeps can be proposed");
            }
            else if (BenchmarkTemplates.Find(proposal.BenchmarkKind, proposal.Scenario) is not { } template)
            {
                outcome.Notes.Add($"ignored benchmark proposal {label}: not in the template table");
            }
            else if (!BenchmarkTemplates.IsSeam(template, candidate.EnclosingSymbol))
            {
                outcome.Notes.Add($"ignored benchmark proposal {label}: {candidate.EnclosingSymbol} is not the template's seam {template.Seam}");
            }
            else
            {
                outcome.AcceptedBenchmarks.Add((proposal, template));
            }
        }
    }

    private static CriticDecision? Read(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("candidateId", out var id) || id.ValueKind != JsonValueKind.String
            || !raw.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.String
            || !Decisions.IsValid(decision.GetString()))
        {
            return null;
        }

        var rationale = raw.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        return string.IsNullOrWhiteSpace(rationale) ? null : new CriticDecision(id.GetString()!, decision.GetString()!, rationale);
    }

    private static string Truncate(string text) => text.Length > 120 ? text[..120] + "…" : text;
}
