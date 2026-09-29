using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SecureYourCode.PerformanceAnalyzer;

/// <summary>
/// PERF004: a growth operation on a static Dictionary/ConcurrentDictionary/List field with no removal operation on that
/// field anywhere in its containing type (including nested types). Not flagged: List&lt;T&gt; indexer assignment (it replaces
/// an element), fields that are also removed from or cleared, and non-static fields.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticCollectionGrowthAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> DictionaryGrowth = ImmutableHashSet.Create("Add", "TryAdd");
    private static readonly ImmutableHashSet<string> ConcurrentDictionaryGrowth = ImmutableHashSet.Create("TryAdd", "GetOrAdd", "AddOrUpdate");
    private static readonly ImmutableHashSet<string> ListGrowth = ImmutableHashSet.Create("Add", "AddRange", "Insert", "InsertRange");
    private static readonly ImmutableHashSet<string> Removal = ImmutableHashSet.Create(
        "Remove", "TryRemove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear");

    private enum CollectionKind
    {
        None,
        Dictionary,
        ConcurrentDictionary,
        List,
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.StaticCollectionGrowth);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = new CollectionTypes(
                start.Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"),
                start.Compilation.GetTypeByMetadataName("System.Collections.Concurrent.ConcurrentDictionary`2"),
                start.Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1"));
            var growth = new ConcurrentBag<(IFieldSymbol Field, Location Location, string Operation)>();
            var removed = new ConcurrentDictionary<IFieldSymbol, bool>(SymbolEqualityComparer.Default);

            start.RegisterOperationAction(operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                if (StaticCollectionField(invocation.Receiver(), types) is not var (field, kind))
                {
                    return;
                }

                var name = invocation.TargetMethod.Name;
                if (Removal.Contains(name) && IsInsideType(operationContext.ContainingSymbol, field.ContainingType))
                {
                    removed[field] = true;
                }
                else if (GrowthMethods(kind).Contains(name))
                {
                    growth.Add((field, invocation.Syntax.GetLocation(), name));
                }
            }, OperationKind.Invocation);

            start.RegisterOperationAction(operationContext =>
            {
                var assignment = (ISimpleAssignmentOperation)operationContext.Operation;
                if (assignment.Target is IPropertyReferenceOperation { Property.IsIndexer: true } indexer
                    && StaticCollectionField(indexer.Instance?.WalkDownConversions(), types) is var (field, kind)
                    && kind is CollectionKind.Dictionary or CollectionKind.ConcurrentDictionary)
                {
                    growth.Add((field, assignment.Syntax.GetLocation(), "indexer assignment"));
                }
            }, OperationKind.SimpleAssignment);

            start.RegisterCompilationEndAction(endContext =>
            {
                foreach (var (field, location, operation) in growth)
                {
                    if (!removed.ContainsKey(field))
                    {
                        endContext.ReportDiagnostic(Diagnostic.Create(
                            Rules.StaticCollectionGrowth, location, field.Name, operation, field.ContainingType.Name));
                    }
                }
            });
        });
    }

    private static ImmutableHashSet<string> GrowthMethods(CollectionKind kind) => kind switch
    {
        CollectionKind.Dictionary => DictionaryGrowth,
        CollectionKind.ConcurrentDictionary => ConcurrentDictionaryGrowth,
        CollectionKind.List => ListGrowth,
        _ => ImmutableHashSet<string>.Empty,
    };

    private static (IFieldSymbol Field, CollectionKind Kind)? StaticCollectionField(IOperation? receiver, CollectionTypes types)
    {
        if (receiver is not IFieldReferenceOperation { Field: { IsStatic: true } field })
        {
            return null;
        }

        var definition = field.Type.OriginalDefinition;
        var kind = SymbolEqualityComparer.Default.Equals(definition, types.Dictionary) ? CollectionKind.Dictionary
            : SymbolEqualityComparer.Default.Equals(definition, types.ConcurrentDictionary) ? CollectionKind.ConcurrentDictionary
            : SymbolEqualityComparer.Default.Equals(definition, types.List) ? CollectionKind.List
            : CollectionKind.None;
        return kind == CollectionKind.None ? null : (field, kind);
    }

    private static bool IsInsideType(ISymbol containingSymbol, INamedTypeSymbol type)
    {
        for (var current = containingSymbol as INamedTypeSymbol ?? containingSymbol.ContainingType; current is not null; current = current.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, type))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CollectionTypes
    {
        public CollectionTypes(INamedTypeSymbol? dictionary, INamedTypeSymbol? concurrentDictionary, INamedTypeSymbol? list)
        {
            Dictionary = dictionary;
            ConcurrentDictionary = concurrentDictionary;
            List = list;
        }

        public INamedTypeSymbol? Dictionary { get; }

        public INamedTypeSymbol? ConcurrentDictionary { get; }

        public INamedTypeSymbol? List { get; }
    }
}
