using SecureYourCode.Agent.Orchestration;

namespace SecureYourCode.Agent.Reporting;

public static class RunStatuses
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

public static class GraphStatuses
{
    public const string Current = "current";
    public const string Stale = "stale";
    public const string None = "none";
}

/// <summary>The canonical report object (plan §4.7): returned by /analyze and, from H6, written as report.json / report.html.</summary>
public sealed class Report
{
    public required RunInfo Run { get; init; }

    public Provenance Provenance { get; } = new();

    /// <summary>Candidates the critic kept or downgraded (downgraded ones carry confidence "low").</summary>
    public List<Candidate> Findings { get; } = [];

    public List<Candidate> RejectedCandidates { get; } = [];

    public List<ReviewerSummary> Reviewers { get; } = [];

    public TokenUsageReport? TokenUsage { get; set; }

    public List<string> Notes { get; } = [];
}

public sealed class RunInfo
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string Status { get; set; } = RunStatuses.Failed;

    public string? Reason { get; set; }

    public string GraphStatus { get; set; } = GraphStatuses.None;

    public List<string> ModelsUsed { get; } = [];
}

public sealed class Provenance
{
    public string? CommitSha { get; set; }

    public bool? Dirty { get; set; }

    public string? Fingerprint { get; set; }

    public string? GraphFingerprint { get; set; }
}

/// <summary>One reviewer session as the report shows it: status, Graphify use, and diagnostics notes.</summary>
public sealed class ReviewerSummary
{
    public required string Reviewer { get; init; }

    public string Status { get; set; } = "not_run";

    public string? Error { get; set; }

    public bool GraphifyAvailable { get; set; }

    public int GraphifyCalls { get; set; }

    public int DeniedToolRequests { get; set; }

    public List<string> Notes { get; } = [];
}
