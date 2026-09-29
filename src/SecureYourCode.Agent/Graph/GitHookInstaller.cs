using System.Text.Json;
using SecureYourCode.Agent.Infrastructure;

namespace SecureYourCode.Agent.Graph;

/// <summary>
/// EnsureHookInstalled() (plan §4.2): installs Husky.Net into the app-owned demo repo, with a post-commit task that
/// notifies the host, and repairs a partial installation. The demo repo's hooks are entirely app-owned, so no chaining.
/// </summary>
public sealed class GitHookInstaller(StatePaths paths, LocalAccessToken token, ILogger<GitHookInstaller> logger)
{
    public const string HuskyVersion = "0.9.1";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(3);

    public async Task EnsureHookInstalledAsync(CancellationToken cancellationToken)
    {
        var repo = paths.RepoPath;
        var postCommit = Path.Combine(repo, ".husky", "post-commit");
        var taskRunner = Path.Combine(repo, ".husky", "task-runner.json");
        var localToken = Path.Combine(repo, ".husky", ".local-token");
        var manifest = Path.Combine(repo, ".config", "dotnet-tools.json");
        // A shell script: always LF, whatever line endings the AppWorkspace checkout uses (core.autocrlf on Windows).
        var postCommitTemplate = (await ReadTemplateAsync("post-commit", cancellationToken)).ReplaceLineEndings("\n");
        var taskRunnerTemplate = await ReadTemplateAsync(HostPlatform.HookTaskRunnerTemplate, cancellationToken);

        var repairs = new List<string>();
        if (ManifestHuskyVersion(manifest) != HuskyVersion)
        {
            repairs.Add("husky tool manifest");
            if (!File.Exists(manifest))
            {
                await DotnetAsync(cancellationToken, "new", "tool-manifest", "--output", ".config");
            }

            var command = ManifestHuskyVersion(manifest) is null ? "install" : "update";
            await DotnetAsync(cancellationToken, "tool", command, "husky", "--version", HuskyVersion);
        }

        var hooksPath = (await Git.TryRunAsync(repo, cancellationToken, "config", "--local", "core.hooksPath")).StandardOutput.Trim();
        if (hooksPath != ".husky" || !File.Exists(Path.Combine(repo, ".husky", "_", "husky.sh")))
        {
            repairs.Add("husky install");
            await DotnetAsync(cancellationToken, "tool", "restore");
            await DotnetAsync(cancellationToken, "husky", "install");
        }

        if (await ReadOrNullAsync(postCommit, cancellationToken) != postCommitTemplate)
        {
            repairs.Add(".husky/post-commit");
            await File.WriteAllTextAsync(postCommit, postCommitTemplate, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(postCommit, (UnixFileMode)0b111_101_101);
            }
        }

        if (await ReadOrNullAsync(taskRunner, cancellationToken) != taskRunnerTemplate)
        {
            repairs.Add(".husky/task-runner.json");
            await File.WriteAllTextAsync(taskRunner, taskRunnerTemplate, cancellationToken);
        }

        if (await ReadOrNullAsync(localToken, cancellationToken) != token.Value)
        {
            repairs.Add(".husky/.local-token");
            SecureFile.WriteOwnerOnly(localToken, token.Value, overwrite: true);
        }

        if (repairs.Count == 0)
        {
            logger.LogInformation("Graph-refresh hook installed in the demo repository");
            return;
        }

        await Git.RunAsync(repo, cancellationToken, "add", ".husky", ".config", ".gitignore");
        var staged = await Git.TryRunAsync(repo, cancellationToken, "diff", "--cached", "--quiet");
        if (staged.ExitCode == 1)
        {
            await Git.CommitAsync(repo, "Add SecureYourCode graph-refresh hook", cancellationToken);
        }

        logger.LogInformation("Graph-refresh hook installed or repaired ({Repairs}){Committed}",
            string.Join(", ", repairs), staged.ExitCode == 1 ? "; committed" : "");
    }

    private async Task<string> ReadTemplateAsync(string name, CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.HookTemplatesDirectory, name);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Hook template '{path}' is missing.");
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private async Task DotnetAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("dotnet", arguments, paths.RepoPath, ToolTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError.Trim()} {result.StandardOutput.Trim()}");
        }
    }

    private static async Task<string?> ReadOrNullAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

    private static string? ManifestHuskyVersion(string manifest)
    {
        if (!File.Exists(manifest))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        return document.RootElement.TryGetProperty("tools", out var tools)
            && tools.TryGetProperty("husky", out var husky)
            && husky.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
    }
}
