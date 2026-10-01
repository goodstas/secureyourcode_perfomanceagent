using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SecureYourCode.PerformanceAnalyzer.Tests;

/// <summary>
/// Runs an analyzer over markup source: {|PERFnnn:span|} marks each expected diagnostic, anything else must be clean.
/// Self-contained on Roslyn itself (H8): the Microsoft.CodeAnalysis.Testing framework was dropped because its transitive
/// packages (DiffPlex, NuGet.* 7.x) are not on every air-gapped feed. The compilation references the running runtime's
/// assemblies and must have no compile errors, so a typo in a test fixture fails the test rather than hiding a diagnostic.
/// </summary>
internal static class Verify<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> RuntimeReferences = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray());

    public static async Task AnalyzerAsync(string source)
    {
        var (code, expected) = ParseMarkup(source);
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest), path: "Test0.cs");
        var compilation = CSharpCompilation.Create(
            "AnalyzerTest",
            [tree],
            RuntimeReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (compileErrors.Count > 0)
        {
            throw new Xunit.Sdk.XunitException("The test source does not compile:\n" + string.Join("\n", compileErrors.Select(Describe)));
        }

        var analyzer = new TAnalyzer();
        var diagnostics = await compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync(CancellationToken.None);
        var actual = diagnostics
            .Select(d => (d.Id, Span: d.Location.SourceSpan))
            .OrderBy(d => d.Span.Start).ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
        var wanted = expected.OrderBy(d => d.Span.Start).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();

        if (!actual.SequenceEqual(wanted))
        {
            var text = tree.GetText();
            throw new Xunit.Sdk.XunitException(
                $"Expected diagnostics:\n{Render(wanted, text)}\nActual diagnostics:\n{Render(actual, text)}");
        }
    }

    /// <summary>Strips {|ID:...|} markers, returning the plain code and the (id, span) each marker covered.</summary>
    private static (string Code, List<(string Id, TextSpan Span)> Expected) ParseMarkup(string markup)
    {
        var code = new StringBuilder(markup.Length);
        var expected = new List<(string, TextSpan)>();
        var open = new Stack<(string Id, int Start)>();
        var i = 0;
        while (i < markup.Length)
        {
            if (markup[i] == '{' && i + 1 < markup.Length && markup[i + 1] == '|')
            {
                var colon = markup.IndexOf(':', i + 2);
                if (colon < 0)
                {
                    throw new FormatException("Unterminated {| marker in test markup.");
                }

                open.Push((markup[(i + 2)..colon], code.Length));
                i = colon + 1;
                continue;
            }

            if (markup[i] == '|' && i + 1 < markup.Length && markup[i + 1] == '}')
            {
                if (open.Count == 0)
                {
                    throw new FormatException("|} without a matching {| in test markup.");
                }

                var (id, start) = open.Pop();
                expected.Add((id, TextSpan.FromBounds(start, code.Length)));
                i += 2;
                continue;
            }

            code.Append(markup[i]);
            i++;
        }

        if (open.Count > 0)
        {
            throw new FormatException("Unclosed {| marker in test markup.");
        }

        return (code.ToString(), expected);
    }

    private static string Render(IEnumerable<(string Id, TextSpan Span)> diagnostics, SourceText text)
    {
        var lines = diagnostics.Select(d =>
        {
            var line = text.Lines.GetLineFromPosition(d.Span.Start);
            return $"  {d.Id} at line {line.LineNumber + 1}, col {d.Span.Start - line.Start + 1}: {text.ToString(d.Span)}";
        }).ToList();
        return lines.Count == 0 ? "  (none)" : string.Join("\n", lines);
    }

    private static string Describe(Diagnostic diagnostic)
    {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        return $"  {diagnostic.Id} at line {position.Line + 1}, col {position.Character + 1}: {diagnostic.GetMessage()}";
    }
}
