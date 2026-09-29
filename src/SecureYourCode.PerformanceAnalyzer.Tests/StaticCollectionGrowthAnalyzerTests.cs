namespace SecureYourCode.PerformanceAnalyzer.Tests;

public class StaticCollectionGrowthAnalyzerTests
{
    [Fact]
    public Task StaticDictionaryAddWithoutRemoval_Reports() => Verify<StaticCollectionGrowthAnalyzer>.AnalyzerAsync("""
        using System.Collections.Generic;

        public static class ReportCache
        {
            private static readonly Dictionary<string, string> Reports = new();

            public static int Count => Reports.Count;

            public static string GetOrAdd(string key)
            {
                if (!Reports.TryGetValue(key, out var report))
                {
                    report = "report " + key;
                    {|PERF004:Reports.Add(key, report)|};
                }

                return report;
            }
        }
        """);

    [Fact]
    public Task StaticDictionaryIndexerAssignmentAndTryAdd_Reports() => Verify<StaticCollectionGrowthAnalyzer>.AnalyzerAsync("""
        using System.Collections.Generic;

        public static class Sessions
        {
            private static readonly Dictionary<int, string> ById = new();

            public static void Track(int id, string name)
            {
                {|PERF004:ById[id] = name|};
                {|PERF004:ById.TryAdd(id + 1, name)|};
            }
        }
        """);

    [Fact]
    public Task StaticConcurrentDictionaryGrowth_Reports() => Verify<StaticCollectionGrowthAnalyzer>.AnalyzerAsync("""
        using System.Collections.Concurrent;

        public sealed class Tenants
        {
            private static readonly ConcurrentDictionary<string, int> Hits = new();

            public int Record(string tenant)
            {
                {|PERF004:Hits.TryAdd(tenant, 0)|};
                {|PERF004:Hits.AddOrUpdate(tenant, 1, (_, n) => n + 1)|};
                return {|PERF004:Hits.GetOrAdd(tenant, 0)|};
            }
        }
        """);

    [Fact]
    public Task StaticListGrowth_Reports() => Verify<StaticCollectionGrowthAnalyzer>.AnalyzerAsync("""
        using System.Collections.Generic;

        public static class AuditLog
        {
            private static readonly List<string> Entries = new();

            public static void Write(string entry, string[] more)
            {
                {|PERF004:Entries.Add(entry)|};
                {|PERF004:Entries.AddRange(more)|};
                {|PERF004:Entries.Insert(0, entry)|};
            }
        }
        """);

    [Fact]
    public Task StaticListIndexerAssignment_NoDiagnostic() => Verify<StaticCollectionGrowthAnalyzer>.AnalyzerAsync("""
        using System.Collections.Generic;

        public static class Slots
        {
            private static readonly List<string> Items = new() { "a", "b", "c" };

            public static void Replace(int index, string value)
            {
                Items[index] = value;
            }
        }
        """);

    [Fact]
    public Task StaticFieldThatIsAlsoRemovedOrCleared_NoDiagnostic() => Verify<StaticCollectionGrowthAnalyzer>.AnalyzerAsync("""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public static class BoundedCache
        {
            private static readonly Dictionary<string, string> Values = new();
            private static readonly ConcurrentDictionary<string, int> Leases = new();
            private static readonly List<string> Recent = new();

            public static void Put(string key, string value)
            {
                Values[key] = value;
                Leases.TryAdd(key, 1);
                Recent.Add(key);
                if (Recent.Count > 100)
                {
                    Values.Remove(Recent[0]);
                    Leases.TryRemove(Recent[0], out _);
                    Recent.RemoveAt(0);
                }
            }

            private sealed class Janitor
            {
                public void Reset() => Values.Clear();
            }
        }
        """);

    [Fact]
    public Task InstanceFields_NoDiagnostic() => Verify<StaticCollectionGrowthAnalyzer>.AnalyzerAsync("""
        using System.Collections.Generic;

        public sealed class PerRequestState
        {
            private readonly Dictionary<string, int> _counts = new();
            private readonly List<string> _log = new();

            public void Track(string key)
            {
                _counts[key] = 1;
                _counts.Add(key + "!", 2);
                _log.Add(key);
            }
        }
        """);
}
