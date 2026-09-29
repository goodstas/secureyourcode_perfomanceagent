using System.Text.Json;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Orchestration;

namespace SecureYourCode.Agent.Reporting;

/// <summary>Report publication failed. The run status is unaffected; /analyze answers HTTP 500 (plan §4.7).</summary>
public sealed class ReportPublicationException(Report report, string message, Exception? inner = null) : Exception(message, inner)
{
    public Report Report { get; } = report;
}

public static class ReportJson
{
    /// <summary>The same shape /analyze returns (web defaults: camelCase), indented for reading.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Serialize(Report report) => JsonSerializer.Serialize(report, Options);
}

/// <summary>
/// Transactional publication (plan §4.7): both files are written into &lt;StateRoot&gt;/reports/&lt;runId&gt;_&lt;shortSha&gt;[-dirty].tmp/,
/// the folder is renamed to its final name only if both writes succeeded, and only then is latest.txt replaced
/// (temp file + rename). A reader sees both files or neither; on failure latest.txt is left unchanged.
/// </summary>
public sealed class FileReportPublisher(StatePaths paths, ILogger<FileReportPublisher> logger) : IReportPublisher
{
    public const string LatestPointer = "latest.txt";

    public static string FolderName(Report report)
    {
        var sha = report.Provenance.CommitSha is { Length: > 0 } commit ? commit[..Math.Min(7, commit.Length)] : "unknown";
        return $"{report.Run.RunId}_{sha}{(report.Provenance.Dirty == true ? "-dirty" : "")}";
    }

    public async Task PublishAsync(Report report, string runDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.ReportsDirectory);
        var name = FolderName(report);
        var temp = Path.Combine(paths.ReportsDirectory, name + ".tmp");
        var final = Path.Combine(paths.ReportsDirectory, name);
        try
        {
            Directory.CreateDirectory(temp);
            await File.WriteAllTextAsync(Path.Combine(temp, "report.json"), ReportJson.Serialize(report), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(temp, "report.html"), HtmlReportRenderer.Render(report), cancellationToken);
            Directory.Move(temp, final);
        }
        catch (Exception exception)
        {
            TryDelete(temp);
            throw new ReportPublicationException(report, $"report publication failed: {exception.Message}", exception);
        }

        var pointer = Path.Combine(paths.ReportsDirectory, LatestPointer);
        var pointerTemp = $"{pointer}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(pointerTemp, name, cancellationToken);
            File.Move(pointerTemp, pointer, overwrite: true);
        }
        catch (Exception exception)
        {
            File.Delete(pointerTemp);
            throw new ReportPublicationException(report, $"reports written to {name}, but {LatestPointer} could not be updated: {exception.Message}", exception);
        }

        logger.LogInformation("Published reports to {Folder}", final);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover .tmp folder is never read as a report.
        }
    }
}
