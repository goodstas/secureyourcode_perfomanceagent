using System.Text.Json;

namespace SecureYourCode.Agent.StaticAnalysis;

/// <summary>A PERF* result from SARIF, with its location in canonical repo-relative form.</summary>
public sealed record StaticDiagnostic(string RuleId, string Message, string File, int StartLine, int EndLine);

/// <summary>A PERF* result whose location cannot be resolved inside RepoPath (a static-analysis data error, plan §4.4).</summary>
public sealed class SarifDataException(string message) : Exception(message);

/// <summary>Parses SARIF 2.1.0 (produced with /p:ErrorLog=&lt;path&gt;%2Cversion=2) and keeps only PERF* results.</summary>
public static class SarifParser
{
    public static IReadOnlyList<StaticDiagnostic> ParsePerfResults(string sarifJson, string repoPath, string projectDirectory)
    {
        using var document = JsonDocument.Parse(sarifJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) || version.GetString() != "2.1.0")
        {
            throw new SarifDataException($"Expected SARIF 2.1.0, found '{(root.TryGetProperty("version", out var v) ? v.ToString() : "none")}'.");
        }

        var diagnostics = new List<StaticDiagnostic>();
        foreach (var run in root.GetProperty("runs").EnumerateArray())
        {
            var baseIds = run.TryGetProperty("originalUriBaseIds", out var ids) ? ids : default;
            if (!run.TryGetProperty("results", out var results))
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                var ruleId = result.TryGetProperty("ruleId", out var id) ? id.GetString() ?? "" : "";
                if (!ruleId.StartsWith("PERF", StringComparison.Ordinal))
                {
                    continue;
                }

                var message = result.TryGetProperty("message", out var m) && m.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
                if (!result.TryGetProperty("locations", out var locations) || locations.GetArrayLength() == 0
                    || !locations[0].TryGetProperty("physicalLocation", out var physical)
                    || !physical.TryGetProperty("artifactLocation", out var artifact)
                    || !artifact.TryGetProperty("uri", out var uri)
                    || !physical.TryGetProperty("region", out var region)
                    || !region.TryGetProperty("startLine", out var start))
                {
                    throw new SarifDataException($"{ruleId} result has no physical location.");
                }

                var uriBaseId = artifact.TryGetProperty("uriBaseId", out var baseId) ? baseId.GetString() : null;
                var file = NormalizeSarifPath(uri.GetString()!, uriBaseId, baseIds, repoPath, projectDirectory);
                var startLine = start.GetInt32();
                var endLine = region.TryGetProperty("endLine", out var end) ? end.GetInt32() : startLine;
                diagnostics.Add(new StaticDiagnostic(ruleId, message, file, startLine, endLine));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// The single canonical form used everywhere (plan §4.4): decode file:// URIs and percent-encoding; resolve a relative
    /// path against its uriBaseId (run.originalUriBaseIds) or the demo project's directory; require it inside RepoPath;
    /// return it relative to RepoPath with '/' separators.
    /// </summary>
    public static string NormalizeSarifPath(string uri, string? uriBaseId, JsonElement originalUriBaseIds, string repoPath, string projectDirectory)
    {
        string path;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && absolute.IsFile)
        {
            path = absolute.LocalPath;
        }
        else
        {
            var relative = Uri.UnescapeDataString(uri).Replace('\\', '/');
            if (Path.IsPathFullyQualified(relative))
            {
                path = relative;
            }
            else
            {
                var baseDirectory = ResolveBaseId(uriBaseId, originalUriBaseIds) ?? projectDirectory;
                path = Path.Combine(baseDirectory, relative);
            }
        }

        var full = Path.GetFullPath(path);
        var inRepo = Path.GetRelativePath(Path.GetFullPath(repoPath), full);
        if (inRepo == "." || inRepo == ".." || inRepo.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(inRepo))
        {
            throw new SarifDataException($"SARIF location '{uri}' does not resolve to a file inside the analyzed repository.");
        }

        return inRepo.Replace('\\', '/');
    }

    private static string? ResolveBaseId(string? uriBaseId, JsonElement originalUriBaseIds)
    {
        if (uriBaseId is null || originalUriBaseIds.ValueKind != JsonValueKind.Object
            || !originalUriBaseIds.TryGetProperty(uriBaseId, out var entry)
            || !entry.TryGetProperty("uri", out var baseUri)
            || !Uri.TryCreate(baseUri.GetString(), UriKind.Absolute, out var parsed)
            || !parsed.IsFile)
        {
            return null;
        }

        return parsed.LocalPath;
    }
}
