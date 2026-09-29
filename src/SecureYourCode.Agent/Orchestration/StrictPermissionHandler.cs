using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>
/// The strict permission handler (plan §4.5; rules proven in H1). Approves only reads inside RepoPath and the allowed
/// Graphify tools without a project_path argument; denies write, shell, URL, memory, and anything unrecognised.
/// Never PermissionHandler.ApproveAll.
/// </summary>
public sealed class StrictPermissionHandler(string repoPath)
{
    public const string GraphifyServerKey = "graphify";

    /// <summary>graphifyServerToolNames (raw MCP names, H1).</summary>
    public static readonly IReadOnlyList<string> GraphifyServerToolNames = ["query_graph", "get_node", "get_neighbors", "shortest_path"];

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _denials = new();

    public int Denied => _denials.Count;

    /// <summary>What was denied ("read &lt;path&gt;", "shell &lt;command&gt;", ...), for the reviewer's report notes.</summary>
    public IReadOnlyList<string> Denials => [.. _denials];

    public Task<PermissionDecision> HandleAsync(PermissionRequest request, PermissionInvocation invocation)
    {
        var approved = request switch
        {
            PermissionRequestRead read => IsInside(read.ResolvedPath ?? read.Path, repoPath),
            // Observed in H1: the MCP permission ToolName is server-qualified ("graphify-shortest_path").
            PermissionRequestMcp mcp => mcp.ServerName == GraphifyServerKey
                && GraphifyServerToolNames.Any(tool => mcp.ToolName == $"{GraphifyServerKey}-{tool}")
                && !HasProjectPath(mcp.Args),
            _ => false,
        };

        if (!approved)
        {
            _denials.Enqueue(request switch
            {
                PermissionRequestRead read => $"read {read.ResolvedPath ?? read.Path}",
                PermissionRequestMcp mcp => $"mcp {mcp.ServerName}/{mcp.ToolName} (arguments: {ArgumentNames(mcp.Args)})",
                PermissionRequestShell shell => $"shell {shell.FullCommandText}",
                PermissionRequestWrite write => $"write {write.FileName}",
                PermissionRequestUrl url => $"url {url.Url}",
                _ => request.Kind,
            });
        }

        return Task.FromResult(approved
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.Reject("Denied by the SecureYourCode strict permission handler: reviewers are read-only."));
    }

    public static bool IsInside(string? path, string root)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative));
    }

    private static bool HasProjectPath(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("project_path", out _);

    private static string ArgumentNames(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } a ? string.Join(", ", a.EnumerateObject().Select(p => p.Name)) : "none";
}
