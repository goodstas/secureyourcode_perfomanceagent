using SecureYourCode.Agent.Reporting;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>What happened during a run, as far as the status rules care (plan §4.7).</summary>
public sealed class RunFacts
{
    /// <summary>The static-analysis build failed, produced no SARIF, or its PERF* data could not be resolved.</summary>
    public string? StaticAnalysisFailure { get; set; }

    /// <summary>The Copilot client (orchestration) could not start.</summary>
    public string? OrchestrationFailure { get; set; }

    /// <summary>"timeout" or "cancelled" when the run was stopped before all specialists finished.</summary>
    public string? StoppedEarly { get; set; }

    public bool SourceChanged { get; set; }

    public List<string> FailedSpecialists { get; } = [];

    /// <summary>"failed" or "incomplete" after the critic's repair attempt, or null when it completed.</summary>
    public string? CriticProblem { get; set; }

    public string? VerificationInfrastructureFailure { get; set; }
}

/// <summary>Run status with the exact rules of plan §4.7. It describes the analysis only, never report publication.</summary>
public static class RunStatusRules
{
    public static (string Status, string? Reason) Compute(RunFacts facts)
    {
        if (facts.StaticAnalysisFailure is { } staticFailure)
        {
            return (RunStatuses.Failed, staticFailure);
        }

        if (facts.OrchestrationFailure is { } orchestrationFailure)
        {
            return (RunStatuses.Failed, orchestrationFailure);
        }

        if (facts.StoppedEarly is { } stopped)
        {
            return (RunStatuses.Failed, stopped);
        }

        var reasons = new List<string>();
        if (facts.SourceChanged)
        {
            reasons.Add("source_changed");
        }

        if (facts.FailedSpecialists.Count > 0)
        {
            reasons.Add("specialist_failed: " + string.Join(", ", facts.FailedSpecialists));
        }

        if (facts.CriticProblem is { } critic)
        {
            reasons.Add($"critic_{critic}");
        }

        if (facts.VerificationInfrastructureFailure is { } verification)
        {
            reasons.Add("verification_failed: " + verification);
        }

        return reasons.Count > 0 ? (RunStatuses.Partial, string.Join("; ", reasons)) : (RunStatuses.Complete, null);
    }
}
