#if !STANDALONE_ANALYZER_HARNESS
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace SecureYourCode.PerformanceAnalyzer.Tests;

/// <summary>
/// Runs an analyzer over markup source with Microsoft.CodeAnalysis.CSharp.Analyzer.Testing (plan §4.3, the default
/// AnalyzerTestHarness=Testing): {|PERFnnn:span|} marks each expected diagnostic, anything else must be clean.
/// StandaloneVerify.cs is the air-gapped alternative with the same markup.
/// </summary>
internal static class Verify<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public static Task AnalyzerAsync(string source) => new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
    {
        TestCode = source,
        ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
    }.RunAsync();
}
#endif
