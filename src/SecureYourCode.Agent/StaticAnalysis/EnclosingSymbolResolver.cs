using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SecureYourCode.Agent.StaticAnalysis;

/// <summary>
/// Host-computed enclosing symbol (plan §4.4): the innermost method, constructor, property, accessor or local function
/// whose span contains the line, written as TypeName.MemberName (nested types joined with '.', no namespace).
/// If no member contains the line, the innermost type; code outside any type (top-level statements) is "Program".
/// </summary>
public static class EnclosingSymbolResolver
{
    public static string Resolve(string sourceText, int line)
    {
        var root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();
        var member = Innermost<SyntaxNode>(root, line, node => node is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax
            or AccessorDeclarationSyntax or LocalFunctionStatementSyntax);
        var type = Innermost<BaseTypeDeclarationSyntax>(root, line, _ => true);
        var typeName = TypeName(member?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() ?? type);
        return member is null ? typeName : $"{typeName}.{MemberName(member)}";
    }

    private static T? Innermost<T>(SyntaxNode root, int line, Func<T, bool> predicate)
        where T : SyntaxNode =>
        root.DescendantNodes()
            .OfType<T>()
            .Where(predicate)
            .Where(node => ContainsLine(node, line))
            .OrderBy(node => node.Span.Length)
            .FirstOrDefault();

    private static bool ContainsLine(SyntaxNode node, int line)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.StartLinePosition.Line + 1 <= line && line <= span.EndLinePosition.Line + 1;
    }

    private static string TypeName(BaseTypeDeclarationSyntax? type) =>
        type is null
            ? "Program"
            : string.Join('.', type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));

    private static string MemberName(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
        DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.Text,
        OperatorDeclarationSyntax op => "operator " + op.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type,
        PropertyDeclarationSyntax property => property.Identifier.Text,
        EventDeclarationSyntax @event => @event.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        AccessorDeclarationSyntax accessor => accessor.Parent?.Parent is { } owner ? MemberName(owner) : accessor.Keyword.Text,
        LocalFunctionStatementSyntax local => local.Identifier.Text,
        _ => member.Kind().ToString(),
    };
}
