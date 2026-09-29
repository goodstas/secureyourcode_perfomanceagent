using System.Text.Json;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>One specialist finding, exactly as the plan's §4.5 output schema defines it (lines are 1-based).</summary>
public sealed record SpecialistFinding(
    string? CandidateId,
    string RuleId,
    string File,
    int StartLine,
    int EndLine,
    string? EnclosingSymbol,
    string Mechanism,
    string Trigger,
    string? GraphPath,
    string Confidence,
    string FixDirection);

public sealed record SpecialistReply(string Reviewer, IReadOnlyList<SpecialistFinding> Findings);

public sealed record CriticDecision(string CandidateId, string Decision, string Rationale);

public sealed record BenchmarkProposal(string CandidateId, string BenchmarkKind, string Scenario);

/// <summary>The critic's reply. Decisions are kept raw (possibly invalid or duplicated); the host validates them per §4.5.</summary>
public sealed record CriticReply(IReadOnlyList<JsonElement> Decisions, IReadOnlyList<BenchmarkProposal> Benchmarks);

/// <summary>Extracts and shape-checks reviewer JSON. A shape error message is suitable for the single repair request.</summary>
public static class ReviewerReplies
{
    public static bool TryParseSpecialist(string reply, string expectedReviewer, out SpecialistReply? parsed, out string error)
    {
        parsed = null;
        if (!TryExtractObject(reply, out var root, out error))
        {
            return false;
        }

        using (root)
        {
            var r = root.RootElement;
            if (!RequireString(r, "reviewer", out var reviewer, ref error) || !RequireString(r, "status", out var status, ref error))
            {
                return false;
            }

            if (reviewer != expectedReviewer || status != "completed")
            {
                error = $"\"reviewer\" must be \"{expectedReviewer}\" and \"status\" must be \"completed\".";
                return false;
            }

            if (!r.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
            {
                error = "\"findings\" must be an array (use [] when there are none).";
                return false;
            }

            var list = new List<SpecialistFinding>();
            var index = 0;
            foreach (var f in findings.EnumerateArray())
            {
                var where = $"findings[{index++}]";
                if (f.ValueKind != JsonValueKind.Object
                    || !RequireString(f, "ruleId", out var ruleId, ref error, where)
                    || !RequireString(f, "file", out var file, ref error, where)
                    || !RequireInt(f, "startLine", out var startLine, ref error, where)
                    || !RequireInt(f, "endLine", out var endLine, ref error, where)
                    || !RequireString(f, "mechanism", out var mechanism, ref error, where)
                    || !RequireString(f, "trigger", out var trigger, ref error, where)
                    || !RequireString(f, "confidence", out var confidence, ref error, where)
                    || !RequireString(f, "fixDirection", out var fixDirection, ref error, where))
                {
                    error = error.Length > 0 ? error : $"{where} must be an object.";
                    return false;
                }

                if (confidence is not (Confidences.Candidate or Confidences.Strong))
                {
                    error = $"{where}.confidence must be \"candidate\" or \"strong\".";
                    return false;
                }

                list.Add(new SpecialistFinding(
                    OptionalString(f, "candidateId"), ruleId, file, startLine, endLine, OptionalString(f, "enclosingSymbol"),
                    mechanism, trigger, OptionalString(f, "graphPath"), confidence, fixDirection));
            }

            parsed = new SpecialistReply(reviewer, list);
            return true;
        }
    }

    public static bool TryParseCritic(string reply, out CriticReply? parsed, out string error)
    {
        parsed = null;
        if (!TryExtractObject(reply, out var root, out error))
        {
            return false;
        }

        using (root)
        {
            var r = root.RootElement;
            if (!RequireString(r, "reviewer", out var reviewer, ref error) || !RequireString(r, "status", out var status, ref error))
            {
                return false;
            }

            if (reviewer != Reviewers.Critic.Name || status != "completed")
            {
                error = $"\"reviewer\" must be \"{Reviewers.Critic.Name}\" and \"status\" must be \"completed\".";
                return false;
            }

            if (!r.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            {
                error = "\"decisions\" must be an array.";
                return false;
            }

            var benchmarks = new List<BenchmarkProposal>();
            if (r.TryGetProperty("benchmarks", out var proposals))
            {
                if (proposals.ValueKind != JsonValueKind.Array)
                {
                    error = "\"benchmarks\" must be an array (use [] when there are none).";
                    return false;
                }

                foreach (var p in proposals.EnumerateArray())
                {
                    if (OptionalString(p, "candidateId") is { } id && OptionalString(p, "benchmarkKind") is { } kind && OptionalString(p, "scenario") is { } scenario)
                    {
                        benchmarks.Add(new BenchmarkProposal(id, kind, scenario));
                    }
                }
            }

            parsed = new CriticReply([.. decisions.EnumerateArray().Select(d => d.Clone())], benchmarks);
            return true;
        }
    }

    /// <summary>Parses the whole reply as JSON, or else the span from the first '{' to the last '}' (code fences, prose).</summary>
    private static bool TryExtractObject(string reply, out JsonDocument document, out string error)
    {
        error = "";
        document = null!;
        foreach (var candidate in new[] { reply.Trim(), Span(reply) })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            try
            {
                document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return true;
                }

                document.Dispose();
            }
            catch (JsonException)
            {
                // Try the next form.
            }
        }

        error = "The reply is not a single JSON object.";
        return false;

        static string Span(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text[start..(end + 1)] : "";
        }
    }

    private static bool RequireString(JsonElement element, string name, out string value, ref string error, string? where = null)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = "";
        error = $"{(where is null ? "" : where + ".")}{name} must be a non-empty string.";
        return false;
    }

    private static bool RequireInt(JsonElement element, string name, out int value, ref string error, string where)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        error = $"{where}.{name} must be an integer.";
        return false;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;
}
