// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>The licenses recorded for one package at one version, as the registry that publishes it declares them (NuGet, npm, Packagist).</summary>
/// <param name="OfTargetVersion">True when they were read on the version the update moves to, false on the current one.</param>
internal sealed record DependencyLicenseEvidence(IReadOnlyList<string> Licenses, bool OfTargetVersion);

/// <summary>Whether the license lets the package be resold within a commercial product, and the license that did.</summary>
internal sealed record DependencyLicenseVerdict(bool Allowed, string? License, bool LicenseOfTargetVersion, string? Refusal);

/// <summary>
/// PLAN-007: an update is written only when a known license allows commercial resale. The list is closed: a license not on
/// it, or no license at all, refuses, with the reason. An SPDX expression is read as such: <c>OR</c> needs one allowed side,
/// <c>AND</c> needs them all, an exception after <c>WITH</c> keeps the license it qualifies. Several licenses recorded for one
/// component must all be allowed. A pure function.
/// </summary>
internal static class DependencyUpdateLicense
{
    private const int MaxExpressionLength = 512;
    private const int MaxIdentifierLength = 128;

    private static readonly HashSet<string> Permissive = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD", "Unlicense", "Zlib",
        "MS-PL", "PostgreSQL", "MPL-2.0", "BSL-1.0"
    };

    /// <param name="unknownReason">What to say when no license is known (the registry's own reason); the default says only that none is known.</param>
    public static DependencyLicenseVerdict Judge(DependencyLicenseEvidence? evidence, string? unknownReason = null)
    {
        var licenses = evidence?.Licenses.Where(license => !string.IsNullOrWhiteSpace(license)).Select(license => license.Trim()).ToList() ?? [];
        if (licenses.Count == 0)
            return new DependencyLicenseVerdict(false, null, false, unknownReason ?? "No license is known for this package: the update is not written.");
        var refused = licenses.FirstOrDefault(license => !Allows(license));
        if (refused is not null)
            return new DependencyLicenseVerdict(false, null, false,
                $"The license '{refused}' is not on the list that allows commercial resale: the update is not written.");
        return new DependencyLicenseVerdict(true, string.Join(", ", licenses.Distinct(StringComparer.OrdinalIgnoreCase)), evidence!.OfTargetVersion, null);
    }

    /// <summary>True when the SPDX expression allows commercial resale. Parentheses are read; anything not understood refuses.</summary>
    public static bool Allows(string expression) => Evaluate(expression, license => Permissive.Contains(license.TrimEnd('+'))) == true;

    /// <summary>
    /// True when the text has the shape of an SPDX license expression: license identifiers joined by <c>AND</c> / <c>OR</c>, an
    /// exception after <c>WITH</c>, parentheses. Whether the licenses allow resale is <see cref="Allows"/>'s question, not this one.
    /// A registry field such as <c>SEE LICENSE IN LICENSE.md</c> or a free sentence is not an expression.
    /// </summary>
    public static bool IsExpression(string expression) =>
        expression.Length <= MaxExpressionLength
        && Evaluate(expression, license => license.Length <= MaxIdentifierLength && license.All(IsIdentifierCharacter) ? true : null) is not null;

    private static bool IsIdentifierCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '+' or ':';

    /// <summary>The value of the expression where each license is judged by <paramref name="license"/> (null for one not understood),
    /// or null when the expression itself is not understood.</summary>
    private static bool? Evaluate(string expression, Func<string, bool?> license)
    {
        var tokens = Tokenize(expression);
        var position = 0;
        var value = ReadOr(tokens, ref position, license);
        return position == tokens.Count ? value : null;
    }

    private static bool? ReadOr(List<string> tokens, ref int position, Func<string, bool?> license)
    {
        var left = ReadAnd(tokens, ref position, license);
        while (left is not null && position < tokens.Count && Is(tokens[position], "OR"))
        {
            position++;
            var right = ReadAnd(tokens, ref position, license);
            if (right is null) return null;
            left = left.Value || right.Value;
        }

        return left;
    }

    private static bool? ReadAnd(List<string> tokens, ref int position, Func<string, bool?> license)
    {
        var left = ReadTerm(tokens, ref position, license);
        while (left is not null && position < tokens.Count && Is(tokens[position], "AND"))
        {
            position++;
            var right = ReadTerm(tokens, ref position, license);
            if (right is null) return null;
            left = left.Value && right.Value;
        }

        return left;
    }

    private static bool? ReadTerm(List<string> tokens, ref int position, Func<string, bool?> license)
    {
        if (position >= tokens.Count) return null;
        var token = tokens[position++];
        if (token == "(")
        {
            var inner = ReadOr(tokens, ref position, license);
            if (inner is null || position >= tokens.Count || tokens[position] != ")") return null;
            position++;
            return inner;
        }

        if (token == ")" || Is(token, "AND") || Is(token, "OR") || Is(token, "WITH")) return null;
        if (position < tokens.Count && Is(tokens[position], "WITH"))
        {
            // An exception widens what a license allows (a linking exception): the license itself decides.
            position++;
            if (position >= tokens.Count || tokens[position] is "(" or ")") return null;
            position++;
        }

        return license(token);
    }

    private static bool Is(string token, string keyword) => string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase);

    private static List<string> Tokenize(string expression) =>
        [.. expression.Replace("(", " ( ", StringComparison.Ordinal).Replace(")", " ) ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
