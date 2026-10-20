// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Shared.Maven;

/// <summary>
/// A Maven version request: <c>LATEST</c> (highest version, snapshots included), <c>RELEASE</c>
/// (highest non-snapshot), a range set such as <c>[1.0,2.0)</c>, <c>(,1.5]</c>, <c>[1.2]</c> or
/// <c>[1.0,1.2),(1.5,)</c>, or a plain version. As in Maven, a plain version is a soft requirement
/// that selects exactly itself; a range picks the highest matching version. Snapshots match a range
/// only when one of its bounds is itself a snapshot (the rule asked for in MNG-3092; Maven 3 itself
/// lets a stray snapshot into any range, which is never what a version tracker wants).
/// </summary>
public sealed class MavenVersionSpec
{
    public const string Latest = "LATEST";
    public const string Release = "RELEASE";

    private readonly string? _exact;
    private readonly IReadOnlyList<Restriction> _restrictions;
    private readonly bool _isLatest;
    private readonly bool _isRelease;

    private MavenVersionSpec(string? exact, IReadOnlyList<Restriction> restrictions, bool isLatest, bool isRelease)
    {
        _exact = exact;
        _restrictions = restrictions;
        _isLatest = isLatest;
        _isRelease = isRelease;
    }

    /// <summary>Parses a version request; a malformed range throws <see cref="FormatException"/>.</summary>
    public static MavenVersionSpec Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        spec = spec.Trim();
        if (string.Equals(spec, Latest, StringComparison.Ordinal))
            return new MavenVersionSpec(null, [], isLatest: true, isRelease: false);
        if (string.Equals(spec, Release, StringComparison.Ordinal))
            return new MavenVersionSpec(null, [], isLatest: false, isRelease: true);
        if (spec[0] is not ('[' or '('))
        {
            if (spec.IndexOfAny(['[', ']', '(', ')', ',']) >= 0)
                throw new FormatException($"Invalid Maven version '{spec}'.");
            return new MavenVersionSpec(spec, [], isLatest: false, isRelease: false);
        }

        var restrictions = new List<Restriction>();
        var rest = spec;
        while (rest.Length > 0)
        {
            var close = rest.IndexOfAny([']', ')']);
            if (rest[0] is not ('[' or '(') || close < 0)
                throw new FormatException($"Invalid Maven version range '{spec}'.");
            restrictions.Add(Restriction.Parse(rest[..(close + 1)], spec));
            rest = rest[(close + 1)..].TrimStart();
            if (rest.Length == 0)
                break;
            if (rest[0] != ',')
                throw new FormatException($"Invalid Maven version range '{spec}'.");
            rest = rest[1..].TrimStart();
            if (rest.Length == 0)
                throw new FormatException($"Invalid Maven version range '{spec}'.");
        }

        for (var i = 1; i < restrictions.Count; i++)
        {
            if (restrictions[i - 1].Upper is not { } upper || restrictions[i].Lower is not { } lower
                || upper.CompareTo(lower) > 0)
                throw new FormatException($"Overlapping or unordered Maven version ranges '{spec}'.");
        }
        return new MavenVersionSpec(null, restrictions, isLatest: false, isRelease: false);
    }

    /// <summary>True when <paramref name="version"/> satisfies this request.</summary>
    public bool Matches(string version)
    {
        if (_isLatest)
            return true;
        if (_isRelease)
            return !MavenSnapshot.IsSnapshot(version);
        if (_exact is not null)
            return new MavenComparableVersion(_exact).Equals(new MavenComparableVersion(version));

        var snapshotAllowed = _restrictions.Any(restriction => restriction.MentionsSnapshot);
        if (MavenSnapshot.IsSnapshot(version) && !snapshotAllowed)
            return false;
        var candidate = new MavenComparableVersion(version);
        return _restrictions.Any(restriction => restriction.Contains(candidate));
    }

    /// <summary>The version this request selects among <paramref name="available"/>, or null.</summary>
    public string? Select(IEnumerable<string> available)
        => available
            .Where(Matches)
            .OrderBy(version => version, MavenComparableVersion.Ordering)
            .LastOrDefault();

    private sealed record Restriction(
        MavenComparableVersion? Lower,
        bool LowerInclusive,
        MavenComparableVersion? Upper,
        bool UpperInclusive)
    {
        public bool MentionsSnapshot =>
            (Lower is not null && MavenSnapshot.IsSnapshot(Lower.Value))
            || (Upper is not null && MavenSnapshot.IsSnapshot(Upper.Value));

        public bool Contains(MavenComparableVersion version)
        {
            if (Lower is not null)
            {
                var low = version.CompareTo(Lower);
                if (low < 0 || (low == 0 && !LowerInclusive))
                    return false;
            }

            if (Upper is not null)
            {
                var high = version.CompareTo(Upper);
                if (high > 0 || (high == 0 && !UpperInclusive))
                    return false;
            }
            return true;
        }

        public static Restriction Parse(string text, string spec)
        {
            var lowerInclusive = text[0] == '[';
            var upperInclusive = text[^1] == ']';
            var body = text[1..^1].Trim();
            var comma = body.IndexOf(',');
            if (comma < 0)
                return Pinned(body, lowerInclusive && upperInclusive, spec);

            if (body.IndexOf(',', comma + 1) >= 0)
                throw new FormatException($"Invalid Maven version range '{spec}'.");
            var lowerText = body[..comma].Trim();
            var upperText = body[(comma + 1)..].Trim();
            var lower = lowerText.Length == 0 ? null : new MavenComparableVersion(lowerText);
            var upper = upperText.Length == 0 ? null : new MavenComparableVersion(upperText);
            if (!AreValidBounds(lower, lowerInclusive, upper, upperInclusive))
                throw new FormatException($"Invalid Maven version range '{spec}'.");
            return new Restriction(lower, lowerInclusive, upper, upperInclusive);
        }

        /// <summary>[1.0] pins one version; (1.0) has no meaning.</summary>
        private static Restriction Pinned(string body, bool bothInclusive, string spec)
        {
            if (!bothInclusive || body.Length == 0)
                throw new FormatException($"Invalid Maven version range '{spec}'.");
            var pinned = new MavenComparableVersion(body);
            return new Restriction(pinned, true, pinned, true);
        }

        /// <summary>An open bound must be exclusive, and a bounded range cannot be inverted.</summary>
        private static bool AreValidBounds(
            MavenComparableVersion? lower, bool lowerInclusive, MavenComparableVersion? upper, bool upperInclusive)
        {
            if (lower is null || upper is null)
                return !(lower is null && lowerInclusive) && !(upper is null && upperInclusive);
            var order = lower.CompareTo(upper);
            return order < 0 || (order == 0 && lowerInclusive && upperInclusive);
        }
    }
}
