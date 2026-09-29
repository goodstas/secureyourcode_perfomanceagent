using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SecureYourCode.PerformanceAnalyzer;

/// <summary>
/// PERF001: a query-executing EF Core call, or a call on a type whose name ends in "Repository" (lower confidence),
/// inside a for/foreach/while loop body. Building an IQueryable without executing it, and calls outside loops, are not flagged.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueryInLoopAnalyzer : DiagnosticAnalyzer
{
    public const string MatchProperty = "match";

    private static readonly ImmutableHashSet<string> QueryExecutingMethods = ImmutableHashSet.Create(
        "ToList", "ToListAsync",
        "First", "FirstAsync", "FirstOrDefault", "FirstOrDefaultAsync",
        "Single", "SingleAsync", "SingleOrDefault", "SingleOrDefaultAsync",
        "Count", "CountAsync");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.QueryInLoop);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var queryable = start.Compilation.GetTypeByMetadataName("System.Linq.IQueryable`1");
            var dbContext = start.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbContext");
            var dbSet = start.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
            start.RegisterOperationAction(
                operationContext => Analyze(operationContext, queryable, dbContext, dbSet),
                OperationKind.Invocation);
        });
    }

    private static void Analyze(
        OperationAnalysisContext context,
        INamedTypeSymbol? queryable,
        INamedTypeSymbol? dbContext,
        INamedTypeSymbol? dbSet)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var match = Classify(invocation, queryable, dbContext, dbSet);
        if (match is null || !IsInLoopBody(invocation))
        {
            return;
        }

        var properties = ImmutableDictionary<string, string?>.Empty.Add(MatchProperty, match);
        var description = match == "repository" ? "repository call, lower confidence" : "query execution";
        context.ReportDiagnostic(Diagnostic.Create(
            Rules.QueryInLoop, invocation.Syntax.GetLocation(), properties, invocation.TargetMethod.Name, description));
    }

    private static string? Classify(
        IInvocationOperation invocation,
        INamedTypeSymbol? queryable,
        INamedTypeSymbol? dbContext,
        INamedTypeSymbol? dbSet)
    {
        var method = invocation.TargetMethod;
        var receiverType = invocation.Receiver()?.Type;

        if (QueryExecutingMethods.Contains(method.Name) && receiverType.ImplementsGeneric(queryable))
        {
            return "ef-query";
        }

        if (method.Name is "SaveChanges" or "SaveChangesAsync" && receiverType.InheritsFromOrEquals(dbContext))
        {
            return "ef-query";
        }

        if (method.Name == "FindAsync" && (receiverType.InheritsFromOrEquals(dbContext) || receiverType.InheritsFromOrEquals(dbSet)))
        {
            return "ef-query";
        }

        return IsRepositoryType(receiverType) || IsRepositoryType(method.ContainingType) ? "repository" : null;
    }

    private static bool IsRepositoryType(ITypeSymbol? type) =>
        type?.Name.EndsWith("Repository", System.StringComparison.Ordinal) == true;

    /// <summary>True when the operation executes once per iteration of an enclosing loop body (not in a lambda or local function).</summary>
    private static bool IsInLoopBody(IOperation operation)
    {
        var span = operation.Syntax.Span;
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case IAnonymousFunctionOperation or ILocalFunctionOperation:
                    return false;
                case ILoopOperation loop when loop.Body.Syntax.Span.Contains(span):
                    return true;
            }
        }

        return false;
    }
}
