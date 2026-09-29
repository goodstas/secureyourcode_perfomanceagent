using Microsoft.Extensions.Logging.Abstractions;
using SecureYourCode.Agent.Infrastructure;

namespace SecureYourCode.Agent.Tests;

/// <summary>A throwaway StateRoot next to the real AppWorkspace; the demo repo is materialized on request.</summary>
public sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        StateRoot = Path.Combine(Path.GetTempPath(), "syc-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(StateRoot);
        Paths = StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = StateRoot });
    }

    public string StateRoot { get; }

    public StatePaths Paths { get; }

    public string RepoPath => Paths.RepoPath;

    public async Task<TestEnvironment> WithDemoRepoAsync(bool analyzerReference = false)
    {
        var materializer = new DemoRepoMaterializer(Paths, NullLogger<DemoRepoMaterializer>.Instance);
        await materializer.EnsureAsync(CancellationToken.None);
        if (analyzerReference)
        {
            Assert.True(File.Exists(Paths.AnalyzerAssembly),
                "Build the analyzer in Release first: dotnet build src/SecureYourCode.PerformanceAnalyzer -c Release");
            await materializer.EnsureAnalyzerReferenceAsync(CancellationToken.None);
        }

        return this;
    }

    public void Dispose()
    {
        if (!Directory.Exists(StateRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(StateRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(StateRoot, recursive: true);
    }
}
