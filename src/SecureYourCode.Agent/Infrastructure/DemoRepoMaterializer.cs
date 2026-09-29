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

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(1);
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs" };

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

            await GitAsync(temp, cancellationToken, "init", "--quiet", "--initial-branch=main");
            await GitAsync(temp, cancellationToken, "add", "--all");
            // App-owned local repo: a fixed identity, so it does not depend on each developer's git configuration.
            await GitAsync(temp, cancellationToken,
                "-c", "user.name=SecureYourCode", "-c", "user.email=secureyourcode@localhost", "-c", "commit.gpgsign=false",
                "commit", "--quiet", "--message", "Initial demo shop");

            Directory.Move(temp, repoPath);
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }

        logger.LogInformation("Demo repository materialized at {RepoPath}", repoPath);
    }

    private static async Task GitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, workingDirectory, GitTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
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
