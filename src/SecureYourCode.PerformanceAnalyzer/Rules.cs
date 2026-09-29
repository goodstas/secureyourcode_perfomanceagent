using Microsoft.CodeAnalysis;

namespace SecureYourCode.PerformanceAnalyzer;

/// <summary>The three hackathon rules (plan section 4.3). Messages describe a potential issue, never a confirmed one.</summary>
internal static class Rules
{
    private const string Category = "Performance";

    public static readonly DiagnosticDescriptor QueryInLoop = new(
        id: "PERF001",
        title: "Query or repository call inside a loop",
        messageFormat: "'{0}' is called inside a loop body ({1}): potential query amplification",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Executing a query or calling a repository once per loop iteration multiplies round trips with the input size. " +
            "Consider loading the data in one batched query before the loop.");

    public static readonly DiagnosticDescriptor UnboundedTaskFanOut = new(
        id: "PERF003",
        title: "Task.WhenAll fan-out sized by input",
        messageFormat: "Task.WhenAll starts one task per element of '{0}', so concurrency grows with input size: potential unbounded fan-out",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Task.WhenAll over a Select of an input-sized collection starts all tasks at once. " +
            "Consider processing the input in sequential bounded batches or limiting concurrency.");

    public static readonly DiagnosticDescriptor StaticCollectionGrowth = new(
        id: "PERF004",
        title: "Static collection grows without removal",
        messageFormat: "Static collection field '{0}' grows via {1} and is never removed from or cleared in '{2}': potential unbounded memory retention",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A static collection that only grows keeps every entry alive for the lifetime of the process. " +
            "Consider bounding it, evicting entries, or using a cache with a size limit.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);
}
