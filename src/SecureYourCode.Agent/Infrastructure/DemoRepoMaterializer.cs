using System.Security;
using System.Text.RegularExpressions;

namespace SecureYourCode.Agent.Infrastructure;

/// <summary>
/// Materializes the demo repository (plan §2, §5): copies test-assets/demo-shop to RepoPath, adds the required
/// .gitignore, runs git init, and commits. It is built in a temp folder and renamed into place only when complete.
/// </summary>
public sealed class DemoRepoMaterializer(StatePaths paths, ILogger<DemoRepoMaterializer> logger)
{
    /// <summary>The demo repo's .gitignore, exactly as plan §5 requires.</summary>
    public const string GitIgnore = """
        bin/
        obj/
        graphify-out/
        .husky/.local-token
        *.user
        .vs/

        """;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs" };

    private static readonly Regex AnalyzerItem =
        new("""<Analyzer\s+Include="[^"]*SecureYourCode\.PerformanceAnalyzer\.dll"\s*/>""", RegexOptions.Compiled);

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var repoPath = paths.RepoPath;
        if (Directory.Exists(repoPath))
        {
            if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            {
                throw new InvalidOperationException(
                    $"RepoPath '{repoPath}' exists but is not a git repository. Delete it to re-materialize the demo repo.");
            }

            logger.LogInformation("Demo repository present at {RepoPath}", repoPath);
            return;
        }

        if (!File.Exists(Path.Combine(paths.DemoShopSource, "DemoShop.csproj")))
        {
            throw new InvalidOperationException($"Demo project not found at '{paths.DemoShopSource}'.");
        }

        var temp = $"{repoPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            CopyDirectory(paths.DemoShopSource, temp);
            await File.WriteAllTextAsync(Path.Combine(temp, ".gitignore"), GitIgnore, cancellationToken);

            await Git.RunAsync(temp, cancellationToken, "init", "--quiet", "--initial-branch=main");
            await Git.RunAsync(temp, cancellationToken, "add", "--all");
            await Git.CommitAsync(temp, "Initial demo shop", cancellationToken);

            Directory.Move(temp, repoPath);
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }

        logger.LogInformation("Demo repository materialized at {RepoPath}", repoPath);
    }

    /// <summary>
    /// Adds, or repairs, the demo project's reference to the Release analyzer build (plan §4.3, H2) and commits the change
    /// in the demo repo. The path is absolute and machine-specific, so it only ever lives in the per-developer demo repo.
    /// </summary>
    public async Task EnsureAnalyzerReferenceAsync(CancellationToken cancellationToken)
    {
        var analyzer = paths.AnalyzerAssembly;
        if (!File.Exists(analyzer))
        {
            throw new InvalidOperationException(
                $"The SecureYourCode analyzer is not built: '{analyzer}' is missing. " +
                "Build it with 'dotnet build src/SecureYourCode.PerformanceAnalyzer -c Release' (tools/setup.py does this).");
        }

        var project = paths.DemoProject;
        var text = await File.ReadAllTextAsync(project, cancellationToken);
        var item = $"<Analyzer Include=\"{SecurityElement.Escape(analyzer)}\" />";
        if (text.Contains(item, StringComparison.Ordinal))
        {
            logger.LogInformation("Demo repository references the analyzer at {Analyzer}", analyzer);
            return;
        }

        string updated;
        if (AnalyzerItem.IsMatch(text))
        {
            updated = AnalyzerItem.Replace(text, _ => item, 1);
        }
        else
        {
            var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException($"'{project}' has no closing </Project> element.");
            }

            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            updated = text.Insert(end, $"  <ItemGroup>{newline}    {item}{newline}  </ItemGroup>{newline}{newline}");
        }

        await File.WriteAllTextAsync(project, updated, cancellationToken);
        await Git.RunAsync(paths.RepoPath, cancellationToken, "add", "DemoShop.csproj");
        await Git.CommitAsync(paths.RepoPath, "Reference the SecureYourCode analyzer", cancellationToken);
        logger.LogInformation("Demo repository now references the analyzer at {Analyzer} (committed)", analyzer);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (!SkippedDirectories.Contains(name))
            {
                CopyDirectory(directory, Path.Combine(destination, name));
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a leftover temp folder never becomes RepoPath.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: git may leave read-only object files on Windows.
        }
    }
}
