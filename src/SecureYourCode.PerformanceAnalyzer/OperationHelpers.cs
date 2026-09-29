using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SecureYourCode.PerformanceAnalyzer;

internal static class OperationHelpers
{
    public static IOperation WalkDownConversions(this IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>The receiver of an invocation: the instance, or the "this" argument of an extension method.</summary>
    public static IOperation? Receiver(this IInvocationOperation invocation)
    {
        if (invocation.Instance is { } instance)
        {
            return instance.WalkDownConversions();
        }

        return invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0
            ? invocation.Arguments[0].Value.WalkDownConversions()
            : null;
    }

    public static bool IsMethodOf(this IInvocationOperation invocation, INamedTypeSymbol? type, string name) =>
        type is not null
        && invocation.TargetMethod.Name == name
        && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, type);

    public static bool InheritsFromOrEquals(this ITypeSymbol? type, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ImplementsGeneric(this ITypeSymbol? type, INamedTypeSymbol? genericInterface)
    {
        if (type is null || genericInterface is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, genericInterface))
        {
            return true;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, genericInterface))
            {
                return true;
            }
        }

        return false;
    }
}
