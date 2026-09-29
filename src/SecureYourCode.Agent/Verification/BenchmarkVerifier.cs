using SecureYourCode.Agent.Orchestration;

namespace SecureYourCode.Agent.Verification;

public sealed class VerificationSummary
{
    public List<string> Notes { get; } = [];

    /// <summary>Set when the verification infrastructure itself failed (template build, process, timeout, invalid output).</summary>
    public string? InfrastructureFailure { get; set; }
}

public interface IVerificationStage
{
    Task<VerificationSummary> VerifyAsync(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<(BenchmarkProposal Proposal, BenchmarkTemplate Template)> acceptedProposals,
        string analysisFingerprint,
        string runDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Host verification and evidence assignment (plan §4.6). Only host-owned templates run, never model-written code.
/// E2 is awarded only at the template's seam, for a template that built and produced three valid fresh-process
/// observations within the time limit, with the fingerprint unchanged before and after, and the acceptance rule met.
/// </summary>
public sealed class BenchmarkVerifier(IBenchmarkRunner runner, IRepositorySnapshot repository, ILogger<BenchmarkVerifier> logger)
    : IVerificationStage
{
    public const string SourceChangedBeforeVerification = "source_changed_before_verification";

    /// <summary>One benchmark set: the template build and all three processes (plan §2).</summary>
    public TimeSpan SetTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public async Task<VerificationSummary> VerifyAsync(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<(BenchmarkProposal Proposal, BenchmarkTemplate Template)> acceptedProposals,
        string analysisFingerprint,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var summary = new VerificationSummary();
        var plan = Plan(candidates, acceptedProposals);
        if (plan.Count == 0)
        {
            return summary;
        }

        // Source binding 1: before verification starts.
        if (await repository.ComputeFingerprintAsync(cancellationToken) != analysisFingerprint)
        {
            foreach (var (candidate, template) in plan)
            {
                candidate.Verification = new VerificationResult("skipped", template.Label, SourceChangedBeforeVerification);
            }

            summary.Notes.Add($"Verification skipped: {SourceChangedBeforeVerification}.");
            return summary;
        }

        var runId = Path.GetFileName(runDirectory);
        var failures = new List<string>();
        foreach (var (candidate, template) in plan)
        {
            var failure = await VerifyOneAsync(candidate, template, analysisFingerprint, runId, cancellationToken);
            if (failure is not null)
            {
                failures.Add($"{template.Label} for {candidate.CandidateId}: {failure}");
            }
        }

        if (failures.Count > 0)
        {
            summary.InfrastructureFailure = string.Join("; ", failures);
        }

        if (candidates.Any(c => c.Evidence == EvidenceLevels.E2))
        {
            summary.Notes.Add("E2 means verified by a host-owned benchmark template bound to a demo seam; in the hackathon it applies to the demo project only.");
        }

        return summary;
    }

    /// <summary>
    /// Required templates for every candidate at their seam whose critic decision is not remove (independent of any proposal),
    /// plus accepted proposals (already limited to kept candidates at the seam), without running a pair twice.
    /// </summary>
    private static List<(Candidate Candidate, BenchmarkTemplate Template)> Plan(
        IReadOnlyList<Candidate> candidates, IReadOnlyList<(BenchmarkProposal Proposal, BenchmarkTemplate Template)> accepted)
    {
        var plan = new List<(Candidate, BenchmarkTemplate)>();
        foreach (var candidate in candidates)
        {
            if (candidate.CriticDecision == Decisions.Remove)
            {
                candidate.Verification = VerificationResult.NotRun("rejected by the critic");
                continue;
            }

            foreach (var template in BenchmarkTemplates.All.Where(t => t.Required && t.RuleId == candidate.RuleId && BenchmarkTemplates.IsSeam(t, candidate.EnclosingSymbol)))
            {
                plan.Add((candidate, template));
            }
        }

        foreach (var (proposal, template) in accepted)
        {
            var candidate = candidates.FirstOrDefault(c => c.CandidateId == proposal.CandidateId);
            if (candidate is not null && BenchmarkTemplates.IsSeam(template, candidate.EnclosingSymbol)
                && !plan.Any(p => p.Item1 == candidate && p.Item2 == template))
            {
                plan.Add((candidate, template));
            }
        }

        return plan;
    }

    /// <summary>Runs one template for one candidate; returns an infrastructure-failure description, or null.</summary>
    private async Task<string?> VerifyOneAsync(
        Candidate candidate, BenchmarkTemplate template, string analysisFingerprint, string runId, CancellationToken cancellationToken)
    {
        if (!BenchmarkTemplates.IsSeam(template, candidate.EnclosingSymbol))
        {
            candidate.Verification = VerificationResult.NotRun($"{candidate.EnclosingSymbol} is not the seam of {template.Label}");
            return null;
        }

        using var setCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setCts.CancelAfter(SetTimeout);
        BenchmarkRun run;
        string before;
        string after;
        try
        {
            // Source binding 2: around each benchmark set.
            before = await repository.ComputeFingerprintAsync(setCts.Token);
            run = await runner.RunAsync(template, runId, candidate.CandidateId, setCts.Token);
            after = await repository.ComputeFingerprintAsync(setCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeout = $"timed out after {SetTimeout.TotalSeconds:0} s (template build and three processes)";
            candidate.Verification = new VerificationResult("failed", template.Label, timeout);
            return timeout;
        }

        if (before != analysisFingerprint || after != analysisFingerprint)
        {
            candidate.Verification = new VerificationResult("not_verified", template.Label,
                "benchmark result discarded: the source changed around the benchmark");
            return null;
        }

        if (run.Error is { } error)
        {
            candidate.Verification = new VerificationResult("failed", template.Label, error);
            return error;
        }

        var (values, invalid) = BenchmarkTemplates.ParseObservations(template, run.Processes);
        if (values is null)
        {
            candidate.Verification = new VerificationResult("failed", template.Label, $"invalid benchmark output: {invalid}");
            return invalid;
        }

        var observations = BenchmarkTemplates.Sizes.Select(n => new BenchmarkObservation(n, values[n])).ToList();
        var (accepted, reason) = BenchmarkTemplates.Evaluate(template, values);
        if (!accepted)
        {
            candidate.Verification = new VerificationResult("not_verified", template.Label,
                $"benchmark ran; acceptance rule not met: {reason}", observations);
            return null;
        }

        candidate.Verification = new VerificationResult("verified", template.Label,
            $"verified by host benchmark template {template.Label}: {reason}", observations, candidate.Evidence);
        candidate.Evidence = EvidenceLevels.E2;
        logger.LogInformation("{Candidate} verified by {Template} (E2)", candidate.CandidateId, template.Label);
        return null;
    }
}
