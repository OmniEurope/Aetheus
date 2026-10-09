// SPDX-License-Identifier: EUPL-1.2
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aetheus.Agent.Core.Operations;

/// <summary>L: aggregate code-quality metrics for a set of C# source files.</summary>
public sealed record ComplexityHotspot(string Path, int Line, string Member, int Cyclomatic);

/// <summary>L: aggregate code-quality metrics for a set of C# source files.</summary>
public sealed record ComplexityReport(
    int TotalMethods,
    int TotalLinesOfCode,
    double AvgCyclomatic,
    int MaxCyclomatic,
    int HighComplexityMethods,
    IReadOnlyList<ComplexityHotspot> Hotspots);

/// <summary>
/// L: real cyclomatic-complexity analysis over C# sources via Roslyn syntax trees (no semantic model,
/// so it runs on raw files without a build). Cyclomatic complexity per method = 1 + the number of
/// decision points (branches, loops, switch arms, catches, short-circuit operators). Lambdas and
/// local functions are independent callable units, so their decisions are not charged twice to
/// both the callable and its enclosing method.
/// </summary>
public static class ComplexityAnalyzer
{
    public const int HighComplexityThreshold = 10;

    public static ComplexityReport Analyze(IEnumerable<(string Path, string Content)> files)
        => Analyze(files.Select(file => new ParsedCSharpSource(
            file.Path,
            file.Content,
            CSharpSyntaxTree.ParseText(file.Content, path: file.Path))));

    public static ComplexityReport Analyze(IEnumerable<ParsedCSharpSource> files)
    {
        var complexities = new List<ComplexityHotspot>();
        var totalNloc = 0;

        foreach (var source in files)
        {
            var (path, content, syntaxTree) = source;
            if (string.IsNullOrWhiteSpace(content)) continue;
            totalNloc += CountNonCommentLines(content);

            var root = syntaxTree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                var body = MemberBody(node);
                if (body is not null)
                {
                    var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    complexities.Add(new ComplexityHotspot(
                        path, line, MemberName(node), CyclomaticComplexity(body)));
                }
            }
        }

        if (complexities.Count == 0)
            return new ComplexityReport(0, totalNloc, 0, 0, 0, []);

        var hotspots = complexities
            .OrderByDescending(item => item.Cyclomatic)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .Take(100)
            .ToArray();

        return new ComplexityReport(
            TotalMethods: complexities.Count,
            TotalLinesOfCode: totalNloc,
            AvgCyclomatic: Math.Round(complexities.Average(item => item.Cyclomatic), 2),
            MaxCyclomatic: hotspots[0].Cyclomatic,
            HighComplexityMethods: complexities.Count(item => item.Cyclomatic > HighComplexityThreshold),
            Hotspots: hotspots);
    }

    // Bodies that count as an independently executable callable unit.
    private static SyntaxNode? MemberBody(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
        AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
        LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
        AnonymousFunctionExpressionSyntax anonymous => anonymous.Body,
        _ => null
    };

    private static string MemberName(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax method => method switch
        {
            MethodDeclarationSyntax declaration => declaration.Identifier.ValueText,
            ConstructorDeclarationSyntax declaration => declaration.Identifier.ValueText,
            DestructorDeclarationSyntax declaration => $"~{declaration.Identifier.ValueText}",
            OperatorDeclarationSyntax declaration => $"operator {declaration.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax declaration =>
                $"operator {declaration.Type}",
            _ => method.Kind().ToString()
        },
        AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        AnonymousFunctionExpressionSyntax => "<lambda>",
        _ => node.Kind().ToString()
    };

    private static int CyclomaticComplexity(SyntaxNode body) =>
        1 + body.DescendantNodesAndSelf(
                descendIntoChildren: node => ReferenceEquals(node, body) || MemberBody(node) is null)
            .Count(node => DecisionKinds.Contains(node.Kind()));

    /// <summary>Each node of these kinds is one more path: branches, loops, case labels and arms, catch
    /// clauses, the conditional operator, and the short-circuit and coalescing operators.</summary>
    private static readonly System.Collections.Frozen.FrozenSet<SyntaxKind> DecisionKinds =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            SyntaxKind.IfStatement, SyntaxKind.WhileStatement, SyntaxKind.DoStatement, SyntaxKind.ForStatement,
            SyntaxKind.ForEachStatement, SyntaxKind.CaseSwitchLabel,
            SyntaxKind.CasePatternSwitchLabel, SyntaxKind.SwitchExpressionArm, SyntaxKind.CatchClause,
            SyntaxKind.ConditionalExpression, SyntaxKind.LogicalAndExpression, SyntaxKind.LogicalOrExpression,
            SyntaxKind.CoalesceExpression
        ]);

    private static int CountNonCommentLines(string content)
    {
        var count = 0;
        var inBlockComment = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (inBlockComment)
            {
                if (line.Contains("*/")) inBlockComment = false;
                continue;
            }
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!line.Contains("*/")) inBlockComment = true;
                continue;
            }
            count++;
        }
        return count;
    }
}
