using System.Security.Cryptography;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Reporting;
using SecureYourCode.Agent.StaticAnalysis;
using SecureYourCode.Agent.Verification;

namespace SecureYourCode.Agent.Orchestration;

public interface IOrchestrator
{
    bool TryEnterGate();

    void ExitGate();

    Task<Report> RunAsync(CancellationToken requestAborted);
}

/// <summary>
/// One /analyze run (plan §4.8). RunAsync owns the whole run: run state, the 30-minute linked timeout, the cancellation
/// reason, and the report written with whatever completed. The gate allows one run at a time.
/// </summary>
public sealed class Orchestrator(
    IRepositorySnapshot repository,
    IGraphSelector graphs,
    IStaticAnalysis staticAnalysis,
    IReviewerClientFactory reviewers,
    IVerificationStage verifier,
    IReportPublisher publisher,
    StatePaths paths,
    OrchestrationTimeouts timeouts,
    TimeProvider time,
    ILogger<Orchestrator> logger) : IOrchestrator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool TryEnterGate() => _gate.Wait(0);

    public void ExitGate() => _gate.Release();

    public async Task<Report> RunAsync(CancellationToken requestAborted)
    {
        var run = new RunState(new Report { Run = new RunInfo { RunId = NewRunId(), StartedAt = time.GetUtcNow() } });
        var runDirectory = Path.Combine(paths.RunsDirectory, run.Report.Run.RunId);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        runCts.CancelAfter(timeouts.Run);
        try
        {
            await ExecutePipelineAsync(run, runDirectory, runCts.Token);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            // Child processes (builds, Graphify, the Copilot runtime) are stopped by their owners on cancellation.
            RecordStop(run, requestAborted.IsCancellationRequested ? "cancelled" : "timeout");
        }

        // The run status is decided here and never depends on publication (plan §4.7).
        Finish(run);
        using var publishCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // fresh: runCts may be cancelled
        try
        {
            await publisher.PublishAsync(run.Report, runDirectory, publishCts.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Report publication failed for run {RunId} (status {Status})", run.Report.Run.RunId, run.Report.Run.Status);
            throw exception as ReportPublicationException ?? new ReportPublicationException(run.Report, exception.Message, exception);
        }

        return run.Report;
    }

    private async Task ExecutePipelineAsync(RunState run, string runDirectory, CancellationToken ct)
    {
        var report = run.Report;

        // 1. Fingerprint, commit and dirty flag.
        var fingerprint = await repository.ComputeFingerprintAsync(ct);
        report.Provenance.Fingerprint = fingerprint;
        var state = await repository.ReadStateAsync(ct);
        report.Provenance.CommitSha = state.CommitSha;
        report.Provenance.Dirty = state.Dirty;

        // 2. Graph: current, stale (used and labelled), or none.
        var (graphStatus, graph) = await graphs.SelectAsync(fingerprint, ct);
        report.Run.GraphStatus = graphStatus;
        report.Provenance.GraphFingerprint = graph?.Fingerprint;
        if (graph is null)
        {
            report.Notes.Add("Graphify unavailable to reviewers: no graph could be built for this run.");
        }

        // 3. Source check after the graph stage.
        if (await repository.ComputeFingerprintAsync(ct) != fingerprint)
        {
            run.Facts.SourceChanged = true;
            report.Notes.Add("The source changed after the graph stage; the run stopped before static analysis.");
            return;
        }

        // 4. Static analysis (E1 source binding inside).
        var analysis = await staticAnalysis.RunAsync(fingerprint, runDirectory, ct);
        switch (analysis.Status)
        {
            case StaticAnalysisStatus.Failed:
                run.Facts.StaticAnalysisFailure = analysis.Reason;
                return;
            case StaticAnalysisStatus.SourceChanged:
                run.Facts.SourceChanged = true;
                report.Notes.Add($"Static analysis is not evidence: {analysis.Reason}; the run stopped before the specialists.");
                return;
        }

        run.Candidates = [.. analysis.BaselineCandidates];

        // 5. Specialists → consolidation → critic → decision semantics.
        await using var client = reviewers.Create();
        try
        {
            await client.StartAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.Facts.OrchestrationFailure = $"orchestration_failed: {exception.Message}";
            return;
        }

        var replies = new List<(ReviewerDefinition, SpecialistReply)>();
        var diagnostics = new Dictionary<string, SpecialistDiagnostics>();
        foreach (var reviewer in Reviewers.Specialists)
        {
            var summary = run.AddReviewer(reviewer.Name, graph is not null);
            var prompt = ReviewerPrompts.Specialist(reviewer, graphStatus, analysis.BaselineCandidates);
            var reply = await RunSessionAsync<SpecialistReply>(client, reviewer, graph, prompt, summary, run, runDirectory,
                text => ReviewerReplies.TryParseSpecialist(text, reviewer.Name, out var parsed, out var error) ? (parsed, null) : (null, error),
                ct);
            if (reply is null)
            {
                run.Facts.FailedSpecialists.Add(reviewer.Name);
            }
            else
            {
                replies.Add((reviewer, reply));
            }
        }

        run.SpecialistsFinished = true;
        run.Candidates = new Consolidator(paths.RepoPath).Consolidate(analysis.BaselineCandidates, replies, diagnostics);
        foreach (var (name, notes) in diagnostics)
        {
            report.Reviewers.First(r => r.Reviewer == name).Notes.AddRange(notes.Notes);
        }

        var criticSummary = run.AddReviewer(Reviewers.Critic.Name, graph is not null);
        var candidateIds = run.Candidates.Select(c => c.CandidateId).ToList();
        var criticReply = await RunSessionAsync<CriticReply>(client, Reviewers.Critic, graph, ReviewerPrompts.Critic(graphStatus, run.Candidates),
            criticSummary, run, runDirectory,
            text =>
            {
                if (!ReviewerReplies.TryParseCritic(text, out var parsed, out var error))
                {
                    return (null, error);
                }

                var problems = CriticDecisions.Problems(run.Candidates, parsed!);
                return (parsed, problems.Count == 0 ? null : string.Join("; ", problems));
            },
            ct,
            repairPrompt: error => ReviewerPrompts.CriticRepair([error], candidateIds),
            acceptAfterRepair: text => ReviewerReplies.TryParseCritic(text, out var parsed, out _) ? parsed : null);

        var outcome = CriticDecisions.Apply(run.Candidates, criticReply);
        criticSummary.Notes.AddRange(outcome.Notes);
        run.CriticApplied = true;
        if (criticReply is null)
        {
            run.Facts.CriticProblem = "failed";
        }
        else if (!outcome.Complete)
        {
            run.Facts.CriticProblem = "incomplete";
            criticSummary.Status = "incomplete";
        }

        foreach (var (proposal, template) in outcome.AcceptedBenchmarks)
        {
            report.Notes.Add($"Benchmark proposal accepted: {template.Label} for {proposal.CandidateId}.");
        }

        // Required seam benchmarks + accepted proposals → host evidence assignment (E2).
        var verification = await verifier.VerifyAsync(run.Candidates, outcome.AcceptedBenchmarks, fingerprint, runDirectory, ct);
        report.Notes.AddRange(verification.Notes);
        if (verification.InfrastructureFailure is { } verificationFailure)
        {
            run.Facts.VerificationInfrastructureFailure = verificationFailure;
        }

        // 6. Final source check: if the source changed, every E2 awarded in this run is reverted.
        if (await repository.ComputeFingerprintAsync(ct) != fingerprint)
        {
            run.Facts.SourceChanged = true;
            RevertE2(run, "verification invalidated: source changed during run");
            report.Notes.Add("The source changed during the run.");
        }
    }

    /// <summary>Reverts every E2 awarded in this run to its previous level (E1 for Roslyn, E0 for specialist findings).</summary>
    private static void RevertE2(RunState run, string reason)
    {
        foreach (var candidate in run.Candidates.Where(c => c.Evidence == EvidenceLevels.E2))
        {
            candidate.Evidence = candidate.Verification.PreviousEvidence
                ?? (candidate.Origin == Origins.Roslyn ? EvidenceLevels.E1 : EvidenceLevels.E0);
            candidate.Verification = candidate.Verification with { Status = "invalidated", Reason = reason };
        }
    }

    /// <summary>
    /// Runs one reviewer session with the per-session timeout: send the task, validate the JSON, and send one repair request
    /// if it is invalid. Returns null when the session failed (invalid after repair, error, or session timeout).
    /// </summary>
    private async Task<T?> RunSessionAsync<T>(
        IReviewerClient client,
        ReviewerDefinition reviewer,
        PublishedGraph? graph,
        string prompt,
        ReviewerSummary summary,
        RunState run,
        string runDirectory,
        Func<string, (T? Parsed, string? Error)> validate,
        CancellationToken ct,
        Func<string, string>? repairPrompt = null,
        Func<string, T?>? acceptAfterRepair = null)
        where T : class
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionCts.CancelAfter(timeouts.Session);
        IReviewerSession? session = null;
        try
        {
            session = await client.CreateSessionAsync(reviewer, graph, sessionCts.Token);
            var reply = await session.SendAsync(prompt, sessionCts.Token);
            await SaveReplyAsync(runDirectory, reviewer, 1, reply);
            var (parsed, error) = validate(reply);
            if (error is not null)
            {
                reply = await session.SendAsync((repairPrompt ?? ReviewerPrompts.Repair)(error), sessionCts.Token);
                await SaveReplyAsync(runDirectory, reviewer, 2, reply);
                (parsed, error) = validate(reply);
                if (error is not null && acceptAfterRepair?.Invoke(reply) is { } usable)
                {
                    // The critic's shape is valid but its decision set is not: the host resolves it (§4.5), no second repair.
                    summary.Status = "completed";
                    summary.Notes.Add($"after repair: {error}");
                    return usable;
                }
            }

            if (error is not null)
            {
                summary.Status = "failed";
                summary.Error = $"invalid reply after the repair request: {error}";
                return null;
            }

            summary.Status = "completed";
            return parsed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            summary.Status = "failed";
            summary.Error = $"session timeout ({timeouts.Session.TotalMinutes:0} min)";
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "{Reviewer} session failed", reviewer.Name);
            summary.Status = "failed";
            summary.Error = exception.Message;
            return null;
        }
        finally
        {
            if (session is not null)
            {
                run.Usage.Add((reviewer.Name, session.Usage));
                summary.GraphifyCalls = session.GraphifyCalls;
                summary.DeniedToolRequests = session.DeniedToolRequests.Count;
                summary.Notes.AddRange(session.DeniedToolRequests.Distinct().Take(10).Select(d => $"denied tool request: {d}"));
                await session.DisposeAsync();
            }
        }
    }

    private static async Task SaveReplyAsync(string runDirectory, ReviewerDefinition reviewer, int attempt, string reply)
    {
        var directory = Path.Combine(runDirectory, "reviewers");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{reviewer.Name}.reply{attempt}.txt"), reply, CancellationToken.None);
    }

    /// <summary>Timeout/cancellation: failed only if it stopped the run before all specialists finished (plan §4.7).</summary>
    private static void RecordStop(RunState run, string reason)
    {
        if (!run.SpecialistsFinished)
        {
            run.Facts.StoppedEarly = reason;
        }
        else if (!run.CriticApplied)
        {
            run.Facts.CriticProblem = $"failed ({reason})";
        }
        else
        {
            run.Facts.VerificationInfrastructureFailure = reason;
        }

        // The final source check never ran, so no E2 from this run can be confirmed.
        RevertE2(run, $"verification not confirmed: the run was stopped ({reason}) before the final source check");

        foreach (var summary in run.Report.Reviewers.Where(r => r.Status == "not_run"))
        {
            summary.Status = "failed";
            summary.Error = reason;
        }

        run.Report.Notes.Add($"The run was stopped: {reason}.");
    }

    private void Finish(RunState run)
    {
        var report = run.Report;
        if (!run.CriticApplied)
        {
            // Whatever completed is still reported; nothing is rejected without a critic decision.
            foreach (var candidate in run.Candidates)
            {
                candidate.Mechanism ??= Consolidator.NotAnalyzed;
                candidate.CriticDecision = Decisions.Keep;
                candidate.CriticRationale = CriticDecisions.NotReviewed;
            }
        }

        report.Findings.AddRange(run.Candidates.Where(c => c.CriticDecision != Decisions.Remove));
        report.RejectedCandidates.AddRange(run.Candidates.Where(c => c.CriticDecision == Decisions.Remove));
        report.TokenUsage = TokenUsageReport.From(run.Usage);
        report.Run.ModelsUsed.AddRange(run.Usage.SelectMany(u => u.Calls).Select(c => c.Model).OfType<string>().Distinct());
        (report.Run.Status, report.Run.Reason) = RunStatusRules.Compute(run.Facts);
        report.Run.FinishedAt = time.GetUtcNow();
    }

    private string NewRunId() =>
        $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";

    private sealed class RunState(Report report)
    {
        public Report Report { get; } = report;

        public RunFacts Facts { get; } = new();

        public List<Candidate> Candidates { get; set; } = [];

        public List<(string Session, IReadOnlyList<UsageRecord> Calls)> Usage { get; } = [];

        public bool SpecialistsFinished { get; set; }

        public bool CriticApplied { get; set; }

        public ReviewerSummary AddReviewer(string name, bool graphifyAvailable)
        {
            var summary = new ReviewerSummary { Reviewer = name, GraphifyAvailable = graphifyAvailable };
            Report.Reviewers.Add(summary);
            return summary;
        }
    }
}
