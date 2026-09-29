using System.Text.Json;
using SecureYourCode.Agent.StaticAnalysis;

namespace SecureYourCode.Agent.Tests;

public class SarifParserTests
{
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "sarif-repo");
    private static readonly string Project = Repo;
    private static readonly JsonElement NoBaseIds = default;

    private static string FileUri(string relative) => new Uri(Path.Combine(Repo, relative)).AbsoluteUri;

    private static string Sarif(string results, string baseIds = "") => $$"""
        { "version": "2.1.0", "runs": [ { {{baseIds}} "results": [ {{results}} ] } ] }
        """;

    private static string Result(string ruleId, string uri, int start, int? end = null, string uriBaseId = "") => $$"""
        { "ruleId": "{{ruleId}}", "message": { "text": "msg {{ruleId}}" },
          "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{uri}}" {{uriBaseId}} },
            "region": { "startLine": {{start}} {{(end is null ? "" : $", \"endLine\": {end}")}} } } } ] }
        """;

    [Fact]
    public void KeepsOnlyPerfResults_WithCanonicalRelativePaths()
    {
        var sarif = Sarif(string.Join(",",
            Result("PERF001", FileUri("Services/OrderSummaryService.cs"), 17, 17),
            Result("CS8618", FileUri("Models/Order.cs"), 3),
            Result("PERF004", FileUri("Services/ReportCache.cs"), 28)));

        var results = SarifParser.ParsePerfResults(sarif, Repo, Project);

        Assert.Equal(2, results.Count);
        Assert.Equal(new StaticDiagnostic("PERF001", "msg PERF001", "Services/OrderSummaryService.cs", 17, 17), results[0]);
        Assert.Equal(new StaticDiagnostic("PERF004", "msg PERF004", "Services/ReportCache.cs", 28, 28), results[1]); // endLine ?? startLine
    }

    [Fact]
    public void DecodesPercentEncodingInFileUris()
    {
        var sarif = Sarif(Result("PERF001", FileUri("My Services/Order Service.cs"), 5));
        Assert.Equal("My Services/Order Service.cs", SarifParser.ParsePerfResults(sarif, Repo, Project).Single().File);
    }

    [Fact]
    public void ResolvesRelativePathsAgainstUriBaseIdOrProjectDirectory()
    {
        var baseUri = new Uri(Path.Combine(Repo, "Services") + Path.DirectorySeparatorChar).AbsoluteUri;
        var baseIds = $$""" "originalUriBaseIds": { "SRCROOT": { "uri": "{{baseUri}}" } }, """;
        var sarif = Sarif(string.Join(",",
            Result("PERF003", "NotificationService.cs", 13, uriBaseId: ", \"uriBaseId\": \"SRCROOT\""),
            Result("PERF001", "Services\\\\InvoiceService.cs", 19)), baseIds);

        var results = SarifParser.ParsePerfResults(sarif, Repo, Project);

        Assert.Equal("Services/NotificationService.cs", results[0].File);
        Assert.Equal("Services/InvoiceService.cs", results[1].File); // backslash separators, resolved against the project dir
    }

    [Fact]
    public void PerfResultOutsideTheRepository_IsADataError()
    {
        var outside = new Uri(Path.Combine(Path.GetTempPath(), "elsewhere", "Evil.cs")).AbsoluteUri;
        var error = Assert.Throws<SarifDataException>(() => SarifParser.ParsePerfResults(Sarif(Result("PERF001", outside, 1)), Repo, Project));
        Assert.Contains("Evil.cs", error.Message);
    }

    [Fact]
    public void RelativePathEscapingTheRepository_IsADataError() =>
        Assert.Throws<SarifDataException>(() => SarifParser.ParsePerfResults(Sarif(Result("PERF001", "../outside.cs", 1)), Repo, Project));

    [Fact]
    public void Sarif1_IsRejected() =>
        Assert.Throws<SarifDataException>(() => SarifParser.ParsePerfResults("""{ "version": "1.0.0", "runs": [] }""", Repo, Project));

    [Fact]
    public void NormalizeSarifPath_IsTheSingleCanonicalForm() =>
        Assert.Equal("Services/ReportCache.cs",
            SarifParser.NormalizeSarifPath(FileUri("Services/ReportCache.cs"), null, NoBaseIds, Repo, Project));
}
