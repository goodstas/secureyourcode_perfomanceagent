using System.Text.Json;

namespace SecureYourCode.Agent.Graph;

/// <summary>
/// graphStructurePredicate, derived in H1 from real graphifyy 0.9.71 --no-cluster output: the root is an object,
/// "nodes" is a non-empty array of objects with a string "id", and "edges" is an array.
/// </summary>
public static class GraphStructure
{
    /// <summary>The graph file's path relative to Graphify's --out folder (graphifyOutputRelativePath, H1).</summary>
    public const string GraphifyOutputRelativePath = "graphify-out/graph.json";

    public static bool IsValidGraphFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            return false;
        }

        try
        {
            using var stream = file.OpenRead();
            using var document = JsonDocument.Parse(stream);
            return IsValid(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsValid(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("nodes", out var nodes)
        && nodes.ValueKind == JsonValueKind.Array
        && nodes.GetArrayLength() > 0
        && nodes.EnumerateArray().All(node =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String)
        && root.TryGetProperty("edges", out var edges)
        && edges.ValueKind == JsonValueKind.Array;
}
