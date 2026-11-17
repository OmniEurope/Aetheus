// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>
/// The one shape of version requirement a bump rewrites: an operator (none, <c>=</c>, <c>^</c>, <c>~</c>, <c>&gt;=</c>) in front of
/// one to three numbers (<c>^4.17.15</c>, <c>~2.9</c>, <c>10.13.0</c>, <c>&gt;=1.2</c>). Everything else (several
/// alternatives, wildcards, tags, stability flags, aliases, addresses) is not parsed, so it is never rewritten by guess.
/// The range of a spec is that of npm and of Composer, which agree on all of this but the two-number tilde.
/// </summary>
internal sealed partial record VersionSpec(string Operator, bool HasVPrefix, IReadOnlyList<int> Numbers)
{
    /// <summary>Which package manager's rules read the spec: they differ on <c>~1.2</c> (npm stops at <c>1.3.0</c>, Composer at <c>2.0.0</c>).</summary>
    public enum Dialect
    {
        Npm,
        Composer
    }

    public static VersionSpec? Parse(string text)
    {
        var match = Shape().Match(text.Trim());
        if (!match.Success) return null;
        var numbers = match.Groups["n"].Captures.Select(capture => capture.Value).ToList();
        // A number that does not fit an int is not a version anyone wrote by hand.
        if (numbers.Any(number => !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
        var op = match.Groups["op"].Value;
        // "1.2" alone is a range in npm and a single version in Composer: not a spec this can rewrite without guessing which.
        if (op is "" or "=" && numbers.Count != 3) return null;
        return new VersionSpec(op, match.Groups["v"].Success, [.. numbers.Select(number => int.Parse(number, CultureInfo.InvariantCulture))]);
    }

    /// <summary>The spec's version as written: <c>4.17.15</c> for <c>^4.17.15</c>.</summary>
    public string Base => string.Join('.', Numbers);

    /// <summary>The same shape with another version: the operator and the <c>v</c> prefix stay as they were written.</summary>
    public string With(string version)
    {
        var bare = version.Trim().TrimStart('v', 'V');
        return Operator + (HasVPrefix ? "v" : string.Empty) + bare;
    }

    /// <summary>Whether <paramref name="version"/>, ordered by <paramref name="ecosystem"/>'s rules, lies in the spec's range. False when it cannot be said.</summary>
    public bool Admits(string ecosystem, Dialect dialect, string version)
    {
        var lower = string.Join('.', [.. Numbers, .. Enumerable.Repeat(0, 3 - Numbers.Count)]);
        if (Operator is "" or "=")
        {
            // An exact spec with fewer than three numbers is not a single version: left to the caller to refuse.
            return Numbers.Count == 3 && VersionComparators.Compare(ecosystem, version, Base) == VersionOrder.Equal;
        }

        if (VersionComparators.Compare(ecosystem, version, lower) is not (VersionOrder.Greater or VersionOrder.Equal)) return false;
        if (Operator == ">=") return true;
        return UpperBound(dialect) is { } upper && VersionComparators.Compare(ecosystem, version, upper) == VersionOrder.Less;
    }

    private string? UpperBound(Dialect dialect)
    {
        var n = Numbers;
        return Operator switch
        {
            "^" when n[0] > 0 => $"{n[0] + 1}.0.0",
            "^" when n.Count == 1 => "1.0.0",
            "^" when n[1] > 0 => $"0.{n[1] + 1}.0",
            "^" when n.Count == 2 => "0.1.0",
            "^" => $"0.0.{n[2] + 1}",
            "~" when n.Count == 1 => $"{n[0] + 1}.0.0",
            "~" when n.Count == 2 && dialect == Dialect.Composer => $"{n[0] + 1}.0.0",
            "~" => $"{n[0]}.{n[1] + 1}.0",
            _ => null
        };
    }

    [GeneratedRegex(@"^(?<op>\^|~|>=|=)?(?<v>v)?(?<n>\d+)(?:\.(?<n>\d+))?(?:\.(?<n>\d+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
