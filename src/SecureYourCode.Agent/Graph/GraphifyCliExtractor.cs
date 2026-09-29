using SecureYourCode.Agent.Infrastructure;

namespace SecureYourCode.Agent.Graph;

/// <summary>Produces a code graph for a repository into an output folder outside it.</summary>
public interface IGraphExtractor
{
    Task<string> GetVersionAsync(CancellationToken cancellationToken);

    Task ExtractAsync(string repoPath, string outputDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// The pinned Graphify CLI: graphify extract &lt;RepoPath&gt; --code-only --no-cluster --out &lt;folder under StateRoot&gt;
/// (flags verified in H1; --out keeps all output outside RepoPath).
/// </summary>
public sealed class GraphifyCliExtractor(StatePaths paths) : IGraphExtractor
{
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private string? _version;

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (_version is not null)
        {
            return _version;
        }

        await _versionGate.WaitAsync(cancellationToken);
        try
        {
            if (_version is null)
            {
                var result = await ProcessRunner.RunAsync(
                    paths.GraphifyPython,
                    ["-c", "import importlib.metadata as m; print(m.version('graphifyy'))"],
                    paths.StateRoot,
                    TimeSpan.FromMinutes(1),
                    cancellationToken);
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    throw new InvalidOperationException($"Could not read the installed graphifyy version: {result.StandardError.Trim()}");
                }

                _version = result.StandardOutput.Trim();
            }

            return _version;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    public async Task ExtractAsync(string repoPath, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var result = await ProcessRunner.RunAsync(
            paths.GraphifyCli,
            ["extract", repoPath, "--code-only", "--no-cluster", "--out", outputDirectory],
            outputDirectory,
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"graphify extract failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }
}
