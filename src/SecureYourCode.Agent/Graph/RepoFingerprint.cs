using System.Security.Cryptography;
using System.Text;
using SecureYourCode.Agent.Infrastructure;

namespace SecureYourCode.Agent.Graph;

/// <summary>
/// Source fingerprint (plan §4.2): SHA-256 over the sorted (path, SHA-256 of content) pairs of every file from
/// git ls-files -co --exclude-standard, excluding .husky/.local-token. Ignored build output never affects it.
/// </summary>
public static class RepoFingerprint
{
    private const string LocalToken = ".husky/.local-token";

    public static async Task<string> ComputeAsync(string repoPath, CancellationToken cancellationToken)
    {
        var listing = await Git.RunAsync(repoPath, cancellationToken, "ls-files", "-z", "-c", "-o", "--exclude-standard");
        var files = listing
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path != LocalToken)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in files)
        {
            var full = Path.Combine(repoPath, relative);
            // A tracked file deleted from the working tree still changes the fingerprint.
            var contentHash = File.Exists(full) ? await HashFileAsync(full, cancellationToken) : "<deleted>";
            fingerprint.AppendData(Encoding.UTF8.GetBytes($"{relative}\0{contentHash}\n"));
        }

        return Convert.ToHexStringLower(fingerprint.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

/// <summary>The analyzed commit and dirty flag (report provenance, plan §4.7).</summary>
public sealed record RepoState(string CommitSha, bool Dirty)
{
    public string ShortSha => CommitSha.Length > 7 ? CommitSha[..7] : CommitSha;

    public static async Task<RepoState> ReadAsync(string repoPath, CancellationToken cancellationToken)
    {
        var sha = (await Git.RunAsync(repoPath, cancellationToken, "rev-parse", "HEAD")).Trim();
        var status = await Git.RunAsync(repoPath, cancellationToken, "status", "--porcelain");
        return new RepoState(sha, status.Trim().Length > 0);
    }
}
