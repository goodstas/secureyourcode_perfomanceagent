using System.Globalization;

namespace SecureYourCode.Agent.Reporting;

/// <summary>One model call's usage, as reported by AssistantUsageEvent (null = not reported by the SDK).</summary>
public sealed record UsageRecord(string? Model, double? InputTokens, double? OutputTokens, double? Cost);

/// <summary>A summed usage field that distinguishes "not reported" from zero (plan §4.7).</summary>
public sealed record UsageTotal(double? Value, int ReportedCalls, int TotalCalls, string Display)
{
    public static UsageTotal Sum(IReadOnlyList<double?> values, string? unit = null)
    {
        var reported = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (reported.Count == 0)
        {
            return new UsageTotal(null, 0, values.Count, "not reported");
        }

        var sum = reported.Sum();
        var text = sum.ToString("0.##", CultureInfo.InvariantCulture) + (unit is null ? "" : " " + unit);
        return new UsageTotal(sum, reported.Count, values.Count,
            reported.Count == values.Count ? text : $"{text} (partial: {reported.Count} of {values.Count} calls reported)");
    }
}

public sealed record SessionUsage(
    string Session,
    int ModelCalls,
    IReadOnlyList<string> Models,
    UsageTotal InputTokens,
    UsageTotal OutputTokens,
    UsageTotal Cost)
{
    public const string CostUnit = "premium request cost units";

    public static SessionUsage From(string session, IReadOnlyList<UsageRecord> calls) => new(
        session,
        calls.Count,
        [.. calls.Select(c => c.Model).OfType<string>().Distinct()],
        UsageTotal.Sum([.. calls.Select(c => c.InputTokens)]),
        UsageTotal.Sum([.. calls.Select(c => c.OutputTokens)]),
        UsageTotal.Sum([.. calls.Select(c => c.Cost)], CostUnit));
}

public sealed record TokenUsageReport(IReadOnlyList<SessionUsage> Sessions, SessionUsage Total)
{
    public static TokenUsageReport From(IReadOnlyList<(string Session, IReadOnlyList<UsageRecord> Calls)> sessions) => new(
        [.. sessions.Select(s => SessionUsage.From(s.Session, s.Calls))],
        SessionUsage.From("total", [.. sessions.SelectMany(s => s.Calls)]));
}
