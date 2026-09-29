using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SecureYourCode.PerformanceAnalyzer;

/// <summary>
/// PERF003: Task.WhenAll(x.Select(...)) where x is a method parameter, field, property or query result, so the task count
/// is driven by input size. x.Chunk(n).Select(...) is still input-sized (about input / n tasks). Not flagged: collection
/// literals and fixed arrays, and sequential bounded batches where the Select source is the loop variable of
/// foreach (var batch in x.Chunk(n)).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnboundedTaskFanOutAnalyzer : DiagnosticAnalyzer
{
    private const int MaxLocalDepth = 4;

    /// <summary>Enumerable operators whose output size still follows the input size.</summary>
    private static readonly ImmutableHashSet<string> SizePreservingOperators = ImmutableHashSet.Create(
        "Chunk", "Where", "Distinct", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "AsEnumerable", "ToList", "ToArray", "Cast", "OfType");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.UnboundedTaskFanOut);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var task = start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var enumerable = start.Compilation.GetTypeByMetadataName("System.Linq.Enumerable");
            if (task is null || enumerable is null)
            {
                return;
            }

            start.RegisterOperationAction(operationContext => Analyze(operationContext, task, enumerable), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol task, INamedTypeSymbol enumerable)
    {
        var whenAll = (IInvocationOperation)context.Operation;
        if (!whenAll.IsMethodOf(task, "WhenAll") || whenAll.Arguments.Length != 1)
        {
            return;
        }

        var argument = whenAll.Arguments[0].Value.WalkDownConversions();
        while (argument is IInvocationOperation { TargetMethod.Name: "ToList" or "ToArray" or "AsEnumerable" } materialize
            && SymbolEqualityComparer.Default.Equals(materialize.TargetMethod.ContainingType, enumerable))
        {
            argument = materialize.Receiver() ?? argument;
        }

        if (argument is not IInvocationOperation select || !select.IsMethodOf(enumerable, "Select") || select.Receiver() is not { } source)
        {
            return;
        }

        while (source is IInvocationOperation op
            && SizePreservingOperators.Contains(op.TargetMethod.Name)
            && SymbolEqualityComparer.Default.Equals(op.TargetMethod.ContainingType, enumerable)
            && op.Receiver() is { } inner)
        {
            source = inner;
        }

        if (IsInputSized(source, enumerable, MaxLocalDepth))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.UnboundedTaskFanOut, whenAll.Syntax.GetLocation(), source.Syntax.ToString()));
        }
    }

    private static bool IsInputSized(IOperation source, INamedTypeSymbol enumerable, int depth)
    {
        switch (source.WalkDownConversions())
        {
            case IParameterReferenceOperation:
            case IFieldReferenceOperation:
            case IPropertyReferenceOperation:
                return true;
            case IAwaitOperation awaited:
                return IsInputSized(awaited.Operation, enumerable, depth);
            case IInvocationOperation invocation:
                // A query result is input-sized, except Enumerable.Range/Repeat with a constant count.
                return !(SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, enumerable)
                    && invocation.TargetMethod.Name is "Range" or "Repeat"
                    && invocation.Arguments.Length == 2
                    && invocation.Arguments[1].Value.ConstantValue.HasValue);
            case ILocalReferenceOperation local when depth > 0:
                return IsInputSizedLocal(local, enumerable, depth - 1);
            default:
                // Collection literals, fixed arrays, object creation, and anything unrecognised: not flagged.
                return false;
        }
    }

    private static bool IsInputSizedLocal(ILocalReferenceOperation local, INamedTypeSymbol enumerable, int depth)
    {
        var semanticModel = local.SemanticModel;
        if (semanticModel is null || local.Local.DeclaringSyntaxReferences.Length != 1)
        {
            return false;
        }

        var declaration = local.Local.DeclaringSyntaxReferences[0].GetSyntax();
        switch (declaration)
        {
            case ForEachStatementSyntax:
                // The element of a sequence: a bounded batch from Chunk(n), or a single item. Not flagged.
                return false;
            case VariableDeclaratorSyntax when semanticModel.GetOperation(declaration) is IVariableDeclaratorOperation declarator:
                var initializer = declarator.Initializer ?? (declarator.Parent as IVariableDeclarationOperation)?.Initializer;
                return initializer is not null && IsInputSized(initializer.Value, enumerable, depth);
            default:
                return false;
        }
    }
}
