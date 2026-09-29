using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.StaticAnalysis;

namespace SecureYourCode.Agent.Tests;

public class EnclosingSymbolAndCandidateTests
{
    private const string Source = """
        namespace Shop.Services;

        public sealed class Outer
        {
            private int _count;

            public Outer(int count)
            {
                _count = count;
            }

            public int Count
            {
                get
                {
                    return _count;
                }
            }

            public void Run()
            {
                Helper();

                void Helper()
                {
                    _count++;
                }
            }

            public sealed class Inner
            {
                public void Work()
                {
                    System.Console.WriteLine();
                }
            }
        }
        """;

    [Theory]
    [InlineData(9, "Outer.Outer")] // constructor
    [InlineData(16, "Outer.Count")] // accessor → its property
    [InlineData(22, "Outer.Run")] // method
    [InlineData(26, "Outer.Helper")] // local function (innermost member)
    [InlineData(34, "Outer.Inner.Work")] // nested type
    [InlineData(5, "Outer")] // field: no member contains the line
    public void ResolvesInnermostMember(int line, string expected) =>
        Assert.Equal(expected, EnclosingSymbolResolver.Resolve(Source, line));

    [Fact]
    public void TopLevelStatementsResolveToProgram() =>
        Assert.Equal("Program", EnclosingSymbolResolver.Resolve("var x = 1;\nSystem.Console.WriteLine(x);\n", 2));

    [Fact]
    public void CandidateIdIsFirst12HexOfSha256()
    {
        var id = CandidateIds.Compute("PERF001", "Services/OrderSummaryService.cs", "OrderSummaryService.BuildSummariesAsync", 17);
        Assert.Matches("^[0-9a-f]{12}$", id);
        Assert.Equal(id, CandidateIds.Compute("PERF001", "Services/OrderSummaryService.cs", "OrderSummaryService.BuildSummariesAsync", 17));
        Assert.NotEqual(id, CandidateIds.Compute("PERF001", "Services/OrderSummaryService.cs", "OrderSummaryService.BuildSummariesAsync", 18));
    }

    [Theory]
    [InlineData("PERF001", Categories.Cpu)]
    [InlineData("LLM-CPU-01", Categories.Cpu)]
    [InlineData("PERF003", Categories.Concurrency)]
    [InlineData("LLM-CONC-02", Categories.Concurrency)]
    [InlineData("PERF004", Categories.Memory)]
    [InlineData("LLM-MEM-00", Categories.Memory)]
    [InlineData("CS8618", null)]
    public void CategoryIsHostMappedFromRuleId(string ruleId, string? expected) =>
        Assert.Equal(expected, Categories.FromRuleId(ruleId));

    [Fact]
    public void BaselineCandidateIsRoslynCandidateE1()
    {
        var candidate = BaselineCandidates.Create(
            new StaticDiagnostic("PERF004", "m", "Services/ReportCache.cs", 28, 28), "ReportCache.GetOrAdd");
        Assert.Equal((Origins.Roslyn, Confidences.Candidate, EvidenceLevels.E1, Categories.Memory),
            (candidate.Origin, candidate.Confidence, candidate.Evidence, candidate.Category));
        Assert.Equal(CandidateIds.Compute("PERF004", "Services/ReportCache.cs", "ReportCache.GetOrAdd", 28), candidate.CandidateId);
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"a"}],"edges":[]}""", true)]
    [InlineData("""{"nodes":[],"edges":[]}""", false)]
    [InlineData("""{"nodes":[{"label":"no id"}],"edges":[]}""", false)]
    [InlineData("""{"nodes":[{"id":"a"}]}""", false)]
    [InlineData("""{"nodes":[{"id":"a"}],"links":[]}""", false)]
    [InlineData("""[1,2]""", false)]
    public void GraphStructurePredicate(string json, bool valid)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(valid, GraphStructure.IsValid(document.RootElement));
    }
}
