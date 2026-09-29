using System.Text.Json;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Orchestration;

namespace SecureYourCode.Agent.StaticAnalysis;

public enum StaticAnalysisStatus
{
    /// <summary>Evidence-valid SARIF; baseline candidates created (E1).</summary>
    Succeeded,

    /// <summary>The build failed, produced no SARIF, or a PERF* location could not be resolved: run status "failed".</summary>
    Failed,

    /// <summary>The source changed around the build: SARIF is not evidence, no candidates; run status "partial / source_changed".</summary>
    SourceChanged,
}

public sealed record StaticAnalysisResult(
    StaticAnalysisStatus Status,
    string? Reason,
    string SarifPath,
    IReadOnlyList<StaticDiagnostic> Diagnostics,
    IReadOnlyList<Candidate> BaselineCandidates);

/// <summary>
/// Static analysis (plan §4.4): rebuilds the demo project with the SecureYourCode analyzer and writes SARIF 2.1.0 to the
/// run folder. SARIF is evidence only if the fingerprint equals analysisFingerprint right before the build and again
/// right after the SARIF is produced (E1 source binding). Every PERF* result becomes a host-owned baseline candidate.
/// </summary>
public sealed class StaticAnalysisRunner(StatePaths paths, ILogger<StaticAnalysisRunner> logger) : IStaticAnalysis
{
    public const string SourceChangedReason = "source_changed_during_static_analysis";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    public async Task<StaticAnalysisResult> RunAsync(string analysisFingerprint, string runDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(runDirectory);
        var sarifPath = Path.Combine(runDirectory, "analysis.sarif");

        if (await RepoFingerprint.ComputeAsync(paths.RepoPath, cancellationToken) != analysisFingerprint)
        {
            return Result(StaticAnalysisStatus.SourceChanged, SourceChangedReason, sarifPath);
        }

        // %2C: MSBuild splits /p: values on ',', so an unescaped ",version=2" would silently produce SARIF 1.0 (H2 finding).
        // No shared compiler server or node reuse: nothing outlives the run or keeps the analyzer DLL locked.
        var build = await ProcessRunner.RunAsync(
            "dotnet",
            ["build", paths.DemoProject, "-t:Rebuild", $"/p:ErrorLog={sarifPath}%2Cversion=2", "/p:UseSharedCompilation=false", "-nodeReuse:false", "-nologo"],
            paths.RepoPath,
            BuildTimeout,
            cancellationToken);

        var after = await RepoFingerprint.ComputeAsync(paths.RepoPath, cancellationToken);
        if (build.ExitCode != 0)
        {
            logger.LogWarning("Static-analysis build failed with exit code {ExitCode}", build.ExitCode);
            return Result(StaticAnalysisStatus.Failed, $"build_failed (exit code {build.ExitCode}): {Tail(build.StandardOutput)}", sarifPath);
        }

        if (!File.Exists(sarifPath))
        {
            return Result(StaticAnalysisStatus.Failed, "sarif_missing", sarifPath);
        }

        if (after != analysisFingerprint)
        {
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "analysis.sarif.validity.json"),
                JsonSerializer.Serialize(new { validForEvidence = false, reason = SourceChangedReason }),
                cancellationToken);
            return Result(StaticAnalysisStatus.SourceChanged, SourceChangedReason, sarifPath);
        }

        IReadOnlyList<StaticDiagnostic> diagnostics;
        try
        {
            diagnostics = SarifParser.ParsePerfResults(
                await File.ReadAllTextAsync(sarifPath, cancellationToken), paths.RepoPath, Path.GetDirectoryName(paths.DemoProject)!);
        }
        catch (SarifDataException exception)
        {
            return Result(StaticAnalysisStatus.Failed, $"sarif_data_error: {exception.Message}", sarifPath);
        }

        var candidates = new List<Candidate>();
        foreach (var diagnostic in diagnostics)
        {
            var source = await File.ReadAllTextAsync(Path.Combine(paths.RepoPath, diagnostic.File), cancellationToken);
            candidates.Add(BaselineCandidates.Create(diagnostic, EnclosingSymbolResolver.Resolve(source, diagnostic.StartLine)));
        }

        logger.LogInformation("Static analysis: {Count} PERF* diagnostics", diagnostics.Count);
        return new StaticAnalysisResult(StaticAnalysisStatus.Succeeded, null, sarifPath, diagnostics, candidates);
    }

    private static StaticAnalysisResult Result(StaticAnalysisStatus status, string reason, string sarifPath) =>
        new(status, reason, sarifPath, [], []);

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(5));
    }
}

public static class BaselineCandidates
{
    /// <summary>One host-owned baseline candidate per PERF* diagnostic: origin roslyn, confidence candidate, evidence E1.</summary>
    public static Candidate Create(StaticDiagnostic diagnostic, string enclosingSymbol) => new()
    {
        CandidateId = CandidateIds.Compute(diagnostic.RuleId, diagnostic.File, enclosingSymbol, diagnostic.StartLine),
        Origin = Origins.Roslyn,
        RuleId = diagnostic.RuleId,
        File = diagnostic.File,
        StartLine = diagnostic.StartLine,
        EndLine = diagnostic.EndLine,
        EnclosingSymbol = enclosingSymbol,
        Category = Categories.FromRuleId(diagnostic.RuleId) ?? throw new InvalidOperationException($"No category for rule {diagnostic.RuleId}."),
        Confidence = Confidences.Candidate,
        Evidence = EvidenceLevels.E1,
        DiagnosticMessage = diagnostic.Message,
    };
}
