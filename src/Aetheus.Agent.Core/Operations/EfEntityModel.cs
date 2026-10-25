// SPDX-License-Identifier: EUPL-1.2
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The entity types of an EF Core model, read from the analysed source (ADR-056): the types exposed as
/// <c>DbSet&lt;T&gt;</c>, configured by an <c>IEntityTypeConfiguration&lt;T&gt;</c> or declared with
/// <c>modelBuilder.Entity&lt;T&gt;()</c>. EF Core need not be referenced by the analysed code: the names are read
/// from the syntax and only the type argument is resolved. Two entity types that reference each other through
/// navigation properties are the standard EF Core bidirectional navigation, not an architecture cycle, and the
/// architecture analyzer uses this model to recognise those edges (and only those).
/// </summary>
internal sealed class EfEntityModel
{
    private readonly HashSet<INamedTypeSymbol> entities;

    private EfEntityModel(HashSet<INamedTypeSymbol> entities) => this.entities = entities;

    public bool IsEntity(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && entities.Contains(named.OriginalDefinition);

    /// <summary>
    /// The entity type a navigation property of an entity type points to, or null when the property is not a
    /// navigation: an instance property of an entity type whose type is an entity type or a single-argument
    /// generic (collection) of one.
    /// </summary>
    public INamedTypeSymbol? NavigationTarget(IPropertySymbol property)
    {
        if (property.IsStatic || property.IsIndexer || !IsEntity(property.ContainingType)) return null;
        var type = property.Type is IArrayTypeSymbol array ? array.ElementType : property.Type;
        if (type is not INamedTypeSymbol named) return null;
        if (IsEntity(named)) return named;
        return named.TypeArguments.Length == 1 && named.TypeArguments[0] is INamedTypeSymbol argument && IsEntity(argument)
            ? argument
            : null;
    }

    /// <summary>
    /// Whether a syntax node belongs to the type or the initializer of a navigation property, which is where a
    /// dependency on the related entity type is a navigation and nothing else.
    /// </summary>
    public bool IsInNavigationDeclaration(SemanticModel semanticModel, SyntaxNode node)
    {
        var property = node.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
        if (property is null) return false;
        var inDeclaredPart = property.Type.Span.Contains(node.Span)
            || (property.Initializer is not null && property.Initializer.Span.Contains(node.Span));
        return inDeclaredPart
            && semanticModel.GetDeclaredSymbol(property) is IPropertySymbol symbol
            && NavigationTarget(symbol) is not null;
    }

    public static EfEntityModel Build(CSharpCompilation compilation, IEnumerable<SyntaxTree> trees)
    {
        var entities = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in trees)
        {
            var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var generic in tree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>())
            {
                if (generic.TypeArgumentList.Arguments.Count != 1 || !DeclaresEntity(generic)) continue;
                if (ResolveClass(semanticModel, generic) is { } named)
                    entities.Add(named.OriginalDefinition);
            }
        }
        return new EfEntityModel(entities);
    }

    /// <summary>
    /// The class named by the type argument of <paramref name="generic"/>. The analysis compilation merges the
    /// global usings of every project, so a name can be ambiguous (the same simple name in two projects); the
    /// candidate whose namespace shares the longest prefix with the namespace of the reference is the one meant.
    /// </summary>
    private static INamedTypeSymbol? ResolveClass(SemanticModel semanticModel, GenericNameSyntax generic)
    {
        var argument = generic.TypeArgumentList.Arguments[0];
        if (semanticModel.GetTypeInfo(argument).Type is INamedTypeSymbol { TypeKind: TypeKind.Class } resolved) return resolved;
        var candidates = semanticModel.GetSymbolInfo(argument).CandidateSymbols
            .OfType<INamedTypeSymbol>().Where(candidate => candidate.TypeKind == TypeKind.Class).ToList();
        if (candidates.Count == 0) return null;
        var here = semanticModel.GetEnclosingSymbol(generic.SpanStart)?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return candidates.OrderByDescending(candidate => CommonPrefixLength(candidate.ContainingNamespace.ToDisplayString(), here))
            .ThenBy(candidate => candidate.ToDisplayString(), StringComparer.Ordinal).First();
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var length = 0;
        while (length < left.Length && length < right.Length && left[length] == right[length]) length++;
        return length;
    }

    private static bool DeclaresEntity(GenericNameSyntax generic) => generic.Identifier.Text switch
    {
        "DbSet" => true,
        "IEntityTypeConfiguration" => generic.Ancestors().OfType<BaseListSyntax>().Any(),
        "Entity" => generic.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax },
        _ => false
    };
}
