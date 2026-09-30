using System.Text.Json;
using SecureYourCode.Evaluate;

// tools/evaluate (plan §5): precision and recall of one report against test-assets/ground-truth.json,
// written to metrics.json in that report's folder. Run it through tools/evaluate.ps1 or tools/evaluate.sh.
//   evaluate                      the latest report: <StateRoot>/reports/<latest.txt>
//   evaluate <report folder>      a specific report folder
//   evaluate <path>/report.json   the same, by its report.json
const string StateRootVariable = "SECUREYOURCODE_STATE_ROOT";

try
{
    if (args.Length > 1 || args is ["-h" or "--help"])
    {
        Console.WriteLine("usage: evaluate [<report folder> | <report.json>]");
        return args.Length > 1 ? 2 : 0;
    }

    var reportJson = args.Length == 1 ? ReportJsonFrom(args[0]) : LatestReportJson();
    var reportFolder = Path.GetDirectoryName(reportJson)!;
    var groundTruthFile = Path.Combine(FindAppWorkspace(), "test-assets", "ground-truth.json");

    var cases = Evaluator.ReadGroundTruth(File.ReadAllText(groundTruthFile));
    var (runId, runStatus, findings) = Evaluator.ReadReport(File.ReadAllText(reportJson));
    var metrics = Evaluator.Evaluate(runId, runStatus, findings, cases);

    var metricsFile = Path.Combine(reportFolder, "metrics.json");
    var temp = metricsFile + ".tmp";
    File.WriteAllText(temp, JsonSerializer.Serialize(metrics, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    File.Move(temp, metricsFile, overwrite: true);

    Console.WriteLine($"report     {Path.GetFileName(reportFolder)} (run status: {runStatus})");
    Console.WriteLine($"TP {metrics.TruePositives}  FP {metrics.FalsePositives}  FN {metrics.FalseNegatives}");
    Console.WriteLine($"precision  {metrics.Precision}");
    Console.WriteLine($"recall     {metrics.Recall}");
    Console.WriteLine($"matches    {string.Join(", ", metrics.Matches.Select(m => $"{m.CaseId}={m.CandidateId}"))}");
    foreach (var note in metrics.Notes)
    {
        Console.WriteLine($"note       {note}");
    }

    Console.WriteLine($"target     precision and recall >= {Evaluator.Target} on a complete run: {(metrics.TargetMet ? "met" : "not met")}");
    Console.WriteLine($"written    {metricsFile}");
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine($"evaluate: {e.Message}");
    return 1;
}

static string ReportJsonFrom(string path)
{
    var full = Path.GetFullPath(path);
    var file = Directory.Exists(full) ? Path.Combine(full, "report.json") : full;
    return File.Exists(file) ? file : throw new FileNotFoundException($"no report.json at '{file}'");
}

static string LatestReportJson()
{
    var configured = Environment.GetEnvironmentVariable(StateRootVariable);
    string stateRoot;
    if (string.IsNullOrWhiteSpace(configured))
    {
        stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".secureyourcode");
    }
    else if (Path.IsPathFullyQualified(configured))
    {
        stateRoot = configured;
    }
    else
    {
        throw new InvalidOperationException($"{StateRootVariable} must be an absolute path, but was '{configured}'.");
    }

    var reports = Path.Combine(stateRoot, "reports");
    var latest = Path.Combine(reports, "latest.txt");
    if (!File.Exists(latest))
    {
        throw new FileNotFoundException($"no published report yet ({latest} does not exist); run /analyze first or pass a report folder");
    }

    return ReportJsonFrom(Path.Combine(reports, File.ReadAllText(latest).Trim()));
}

static string FindAppWorkspace()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SecureYourCode.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException($"SecureYourCode.slnx was not found above '{AppContext.BaseDirectory}'.");
}
