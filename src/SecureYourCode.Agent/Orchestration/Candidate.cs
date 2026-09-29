using System.Security.Cryptography;
using System.Text;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>
/// A finding candidate (plan §4.4). Identity and evidence fields are host-owned and immutable once created;
/// specialists may only enrich the analysis fields (added in H4).
/// </summary>
public sealed class Candidate
{
    public required string CandidateId { get; init; }

    public required string Origin { get; init; }

    public required string RuleId { get; init; }

    public required string File { get; init; }

    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public required string EnclosingSymbol { get; init; }

    public required string Category { get; init; }

    public required string Confidence { get; set; }

    public required string Evidence { get; set; }

    /// <summary>The analyzer's message for a Roslyn baseline candidate (host-owned).</summary>
    public string? DiagnosticMessage { get; init; }
}

public static class Origins
{
    public const string Roslyn = "roslyn";
    public const string Specialist = "specialist";
}

public static class Confidences
{
    public const string Low = "low";
    public const string Candidate = "candidate";
    public const string Strong = "strong";
}

public static class EvidenceLevels
{
    public const string E0 = "E0";
    public const string E1 = "E1";
    public const string E2 = "E2";
}

/// <summary>Host-owned category mapping from the rule ID (plan §4.4); never taken from a model.</summary>
public static class Categories
{
    public const string Cpu = "CPU & Amplification";
    public const string Concurrency = "Concurrency";
    public const string Memory = "Memory & Allocation";

    public static string? FromRuleId(string ruleId) => ruleId switch
    {
        "PERF001" => Cpu,
        "PERF003" => Concurrency,
        "PERF004" => Memory,
        _ when ruleId.StartsWith("LLM-CPU-", StringComparison.Ordinal) => Cpu,
        _ when ruleId.StartsWith("LLM-CONC-", StringComparison.Ordinal) => Concurrency,
        _ when ruleId.StartsWith("LLM-MEM-", StringComparison.Ordinal) => Memory,
        _ => null,
    };
}

public static class CandidateIds
{
    /// <summary>First 12 hex characters of SHA-256 of "ruleId|file|enclosingSymbol|startLine" (plan §4.4).</summary>
    public static string Compute(string ruleId, string file, string enclosingSymbol, int startLine)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ruleId}|{file}|{enclosingSymbol}|{startLine}"));
        return Convert.ToHexStringLower(hash)[..12];
    }
}
