using System.Security;
using SecureYourCode.Agent.Infrastructure;

namespace SecureYourCode.Agent.Verification;

public interface IBenchmarkRunner
{
    /// <summary>Copies, builds and runs one template for every n in its own process. The caller owns the timeout.</summary>
    Task<BenchmarkRun> RunAsync(BenchmarkTemplate template, string runId, string candidateId, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a host-owned template (plan §4.6): copies it to &lt;StateRoot&gt;/verification/&lt;runId&gt;/&lt;candidateId&gt;/, points its
/// ProjectReference at the demo project's absolute path, builds it once in Release, then runs `dotnet &lt;Kind&gt;.dll --n &lt;n&gt;`
/// for each n in a fresh process. Nothing is written into the demo repo except its gitignored build output.
/// </summary>
public sealed class DotnetBenchmarkRunner(StatePaths paths) : IBenchmarkRunner
{
    private const string DemoProjectPlaceholder = "__DEMO_PROJECT__";

    public async Task<BenchmarkRun> RunAsync(BenchmarkTemplate template, string runId, string candidateId, CancellationToken cancellationToken)
    {
        var source = Path.Combine(paths.VerificationTemplatesDirectory, template.Kind);
        var work = Path.Combine(paths.VerificationDirectory, runId, candidateId);
        if (!File.Exists(Path.Combine(source, $"{template.Kind}.csproj")))
        {
            return new BenchmarkRun($"template {template.Kind} not found at {source}", []);
        }

        Directory.CreateDirectory(work);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(work, Path.GetFileName(file)), overwrite: true);
        }

        var project = Path.Combine(work, $"{template.Kind}.csproj");
        var text = await File.ReadAllTextAsync(project, cancellationToken);
        await File.WriteAllTextAsync(project, text.Replace(DemoProjectPlaceholder, SecurityElement.Escape(paths.DemoProject)), cancellationToken);

        var build = await ProcessRunner.RunAsync(
            "dotnet",
            ["build", project, "-c", "Release", "-nologo", "/p:UseSharedCompilation=false", "-nodeReuse:false"],
            work,
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        if (build.ExitCode != 0)
        {
            var errors = build.StandardOutput.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(3);
            return new BenchmarkRun($"template build failed (exit code {build.ExitCode}): {string.Join(" | ", errors).Trim()}", []);
        }

        var assembly = Path.Combine(work, "bin", "Release", "net10.0", $"{template.Kind}.dll");
        var processes = new List<BenchmarkProcess>();
        foreach (var n in BenchmarkTemplates.Sizes)
        {
            var result = await ProcessRunner.RunAsync(
                "dotnet", [assembly, "--n", n.ToString(System.Globalization.CultureInfo.InvariantCulture)], work, Timeout.InfiniteTimeSpan, cancellationToken);
            processes.Add(new BenchmarkProcess(n, result.ExitCode, result.StandardOutput, result.StandardError));
        }

        return new BenchmarkRun(null, processes);
    }
}
