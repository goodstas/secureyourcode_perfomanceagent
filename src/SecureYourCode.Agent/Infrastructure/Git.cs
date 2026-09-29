namespace SecureYourCode.Agent.Infrastructure;

/// <summary>Runs git against a repository without a shell and fails loudly on a non-zero exit code.</summary>
public static class Git
{
    /// <summary>App-owned demo repo commits use a fixed identity, independent of each developer's git configuration.</summary>
    public static readonly string[] AppIdentity =
        ["-c", "user.name=SecureYourCode", "-c", "user.email=secureyourcode@localhost", "-c", "commit.gpgsign=false"];

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public static async Task<string> RunAsync(string repository, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await TryRunAsync(repository, cancellationToken, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    public static Task<ProcessResult> TryRunAsync(string repository, CancellationToken cancellationToken, params string[] arguments) =>
        ProcessRunner.RunAsync("git", arguments, repository, Timeout, cancellationToken);

    public static Task CommitAsync(string repository, string message, CancellationToken cancellationToken) =>
        RunAsync(repository, cancellationToken, [.. AppIdentity, "commit", "--quiet", "--message", message]);
}
