using System.Globalization;
using System.Text.Json;

namespace SecureYourCode.Agent.Verification;

/// <summary>A host-owned benchmark template, bound to the one seam it may verify (plan §4.6).</summary>
public sealed record BenchmarkTemplate(string Kind, string Scenario, string Seam, string RuleId, string Metric, bool Required)
{
    public string Label => $"{Kind}/{Scenario}";
}

public static class BenchmarkTemplates
{
    /// <summary>Every n is measured in a fresh process, in this order.</summary>
    public static readonly IReadOnlyList<int> Sizes = [10, 100, 1000];

    public static readonly IReadOnlyList<BenchmarkTemplate> All =
    [
        new("RepositoryCallAmplification", "OrderCustomerLookup", "OrderSummaryService.BuildSummariesAsync", "PERF001", "repository calls", Required: true),
        new("CollectionGrowth", "ReportCache", "ReportCache.GetOrAdd", "PERF004", "entries retained", Required: false),
        new("TaskFanOut", "NotificationRecipients", "NotificationService.NotifyAllAsync", "PERF003", "tasks started", Required: false),
    ];

    public static BenchmarkTemplate? Find(string kind, string scenario) =>
        All.FirstOrDefault(t => t.Kind == kind && t.Scenario == scenario);

    /// <summary>Seam matching compares TypeName.MemberName, ignoring namespaces (plan §4.6).</summary>
    public static bool IsSeam(BenchmarkTemplate template, string enclosingSymbol)
    {
        var parts = enclosingSymbol.Split('.');
        var typeAndMember = parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : enclosingSymbol;
        return typeAndMember == template.Seam;
    }

    /// <summary>
    /// Validates each process's output: exit code 0, exactly one observation, the requested n, the template's metric, and
    /// value ≥ 0. Returns the value per n, or the first problem.
    /// </summary>
    public static (IReadOnlyDictionary<int, double>? Values, string? Error) ParseObservations(
        BenchmarkTemplate template, IReadOnlyList<BenchmarkProcess> processes)
    {
        var values = new Dictionary<int, double>();
        foreach (var n in Sizes)
        {
            var process = processes.FirstOrDefault(p => p.N == n);
            if (process is null)
            {
                return (null, $"no process ran for n={n}");
            }

            if (process.ExitCode != 0)
            {
                return (null, $"process for n={n} exited with code {process.ExitCode}: {Truncate(process.StandardError)}");
            }

            var lines = process.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != 1)
            {
                return (null, $"process for n={n} printed {lines.Length} observations instead of exactly one");
            }

            try
            {
                using var document = JsonDocument.Parse(lines[0]);
                var root = document.RootElement;
                if (!root.TryGetProperty("n", out var reportedN) || !reportedN.TryGetInt32(out var nValue) || nValue != n)
                {
                    return (null, $"observation for n={n} reports a different n");
                }

                if (!root.TryGetProperty("metric", out var metric) || metric.GetString() != template.Metric)
                {
                    return (null, $"observation for n={n} has metric '{(root.TryGetProperty("metric", out var m) ? m.ToString() : "")}', expected '{template.Metric}'");
                }

                if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number || value.GetDouble() < 0)
                {
                    return (null, $"observation for n={n} has no non-negative numeric value");
                }

                values[n] = value.GetDouble();
            }
            catch (JsonException)
            {
                return (null, $"observation for n={n} is not valid JSON");
            }
        }

        return (values, null);
    }

    /// <summary>The kind-specific E2 acceptance rule (plan §4.6).</summary>
    public static (bool Accepted, string Reason) Evaluate(BenchmarkTemplate template, IReadOnlyDictionary<int, double> values)
    {
        foreach (var n in Sizes)
        {
            if (values[n] < 0.9 * n)
            {
                return (false, $"value({n})={Format(values[n])} < 0.9·{n}");
            }
        }

        if (template.Kind == "RepositoryCallAmplification" && values[1000] < 50 * values[10])
        {
            return (false, $"value(1000)={Format(values[1000])} < 50·value(10)={Format(50 * values[10])}");
        }

        return (true, template.Kind == "RepositoryCallAmplification"
            ? "value(n) ≥ 0.9·n for every n, and value(1000) ≥ 50·value(10)"
            : "value(n) ≥ 0.9·n for every n");
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Truncate(string text) => text.Length > 200 ? text[..200] + "…" : text.Trim();
}

/// <summary>One benchmark process: the requested n, its exit code and output.</summary>
public sealed record BenchmarkProcess(int N, int ExitCode, string StandardOutput, string StandardError);

/// <summary>A template build and its processes, or the build/infrastructure error.</summary>
public sealed record BenchmarkRun(string? Error, IReadOnlyList<BenchmarkProcess> Processes);
