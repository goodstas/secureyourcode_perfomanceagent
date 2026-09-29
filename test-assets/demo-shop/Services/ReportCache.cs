using DemoShop.Models;

namespace DemoShop.Services;

public static class ReportCache
{
    private static readonly Dictionary<string, Report> Reports = new();
    private static readonly Lock Gate = new();

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Reports.Count;
            }
        }
    }

    public static Report GetOrAdd(string key)
    {
        lock (Gate)
        {
            if (!Reports.TryGetValue(key, out var report))
            {
                report = new Report(key, $"Report for {key}", DateTime.UtcNow);
                Reports.Add(key, report);
            }

            return report;
        }
    }
}
