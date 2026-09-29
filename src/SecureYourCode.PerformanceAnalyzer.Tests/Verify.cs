using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace SecureYourCode.PerformanceAnalyzer.Tests;

/// <summary>Runs an analyzer over markup source: {|PERFnnn:span|} marks each expected diagnostic, anything else must be clean.</summary>
internal static class Verify<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public static Task AnalyzerAsync(string source) => new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
    {
        TestCode = source,
        ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
    }.RunAsync();
}
