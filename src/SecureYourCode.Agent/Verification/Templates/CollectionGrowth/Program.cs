// CollectionGrowth / ReportCache (plan §4.6).
// Seam: ReportCache.GetOrAdd. Measures entries retained in the static cache after n distinct keys and a forced full GC.
// Runs in a fresh process per n, so the static cache starts empty. Never adds a reset to demo code.
// Prints exactly one JSON line: { "n": <n>, "metric": "entries retained", "value": <count> }.
using System.Text.Json;
using DemoShop.Services;

var n = args.Length == 2 && args[0] == "--n" && int.TryParse(args[1], out var parsed) && parsed > 0
    ? parsed
    : throw new ArgumentException("usage: --n <positive integer>");

for (var i = 0; i < n; i++)
{
    ReportCache.GetOrAdd($"benchmark-key-{i}");
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

Console.WriteLine(JsonSerializer.Serialize(new { n, metric = "entries retained", value = ReportCache.Count }));
