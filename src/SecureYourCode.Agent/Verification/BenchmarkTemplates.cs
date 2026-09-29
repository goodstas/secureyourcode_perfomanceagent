namespace SecureYourCode.Agent.Verification;

/// <summary>A host-owned benchmark template, bound to the one seam it may verify (plan §4.6).</summary>
public sealed record BenchmarkTemplate(string Kind, string Scenario, string Seam, string RuleId, bool Required);

public static class BenchmarkTemplates
{
    public static readonly IReadOnlyList<BenchmarkTemplate> All =
    [
        new("RepositoryCallAmplification", "OrderCustomerLookup", "OrderSummaryService.BuildSummariesAsync", "PERF001", Required: true),
        new("CollectionGrowth", "ReportCache", "ReportCache.GetOrAdd", "PERF004", Required: false),
        new("TaskFanOut", "NotificationRecipients", "NotificationService.NotifyAllAsync", "PERF003", Required: false),
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
}
