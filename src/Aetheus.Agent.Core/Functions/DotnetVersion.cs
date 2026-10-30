// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Functions;

/// <summary>A .NET runtime version as Microsoft's release metadata and the runtime packs spell it:
/// <c>major.minor.patch</c> with an optional SemVer prerelease (<c>11.0.0-rc.1</c>). A prerelease sorts
/// before the release it leads to, so a runtime candidate is older than the final release that fixes it.</summary>
internal sealed record DotnetVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<DotnetVersion>
{
    /// <summary>The release channel the version belongs to, <c>major.minor</c>: the unit Microsoft publishes
    /// its release metadata by.</summary>
    public string Channel => $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The version, or null when the text is not <c>major.minor.patch[-prerelease]</c>.</summary>
    public static DotnetVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? text : text[..dash];
        var prerelease = dash < 0 ? null : text[(dash + 1)..];
        var numbers = core.Split('.');
        if (numbers.Length != 3 || prerelease is { Length: 0 })
            return null;
        var parsed = new int[3];
        for (var index = 0; index < 3; index++)
        {
            if (numbers[index].Length == 0 || !numbers[index].All(char.IsAsciiDigit)
                || !int.TryParse(numbers[index], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[index]))
                return null;
        }
        return new DotnetVersion(parsed[0], parsed[1], parsed[2], prerelease);
    }

    public int CompareTo(DotnetVersion? other)
    {
        if (other is null)
            return 1;
        var core = Major.CompareTo(other.Major);
        if (core == 0)
            core = Minor.CompareTo(other.Minor);
        if (core == 0)
            core = Patch.CompareTo(other.Patch);
        if (core != 0)
            return core;
        if (Prerelease is null || other.Prerelease is null)
            return (Prerelease is null ? 1 : 0) - (other.Prerelease is null ? 1 : 0);
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public static bool operator <(DotnetVersion left, DotnetVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(DotnetVersion left, DotnetVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(DotnetVersion left, DotnetVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(DotnetVersion left, DotnetVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}."
        + $"{Patch.ToString(CultureInfo.InvariantCulture)}{(Prerelease is null ? string.Empty : "-" + Prerelease)}";

    /// <summary>SemVer 2.0.0 section 11: numeric identifiers compare as numbers and sort before the
    /// alphanumeric ones, and the shorter list sorts first when every shared identifier is equal.</summary>
    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var leftNumeric = leftParts[index].All(char.IsAsciiDigit) && leftParts[index].Length > 0;
            var rightNumeric = rightParts[index].All(char.IsAsciiDigit) && rightParts[index].Length > 0;
            var order = leftNumeric && rightNumeric
                ? CompareNumbers(leftParts[index], rightParts[index])
                : leftNumeric != rightNumeric
                    ? (leftNumeric ? -1 : 1)
                    : string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (order != 0)
                return Math.Sign(order);
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    /// <summary>Digit strings compared as numbers of any length: a longer string without leading zeros is larger.</summary>
    private static int CompareNumbers(string left, string right)
    {
        var trimmedLeft = left.TrimStart('0');
        var trimmedRight = right.TrimStart('0');
        return trimmedLeft.Length != trimmedRight.Length
            ? trimmedLeft.Length.CompareTo(trimmedRight.Length)
            : string.CompareOrdinal(trimmedLeft, trimmedRight);
    }
}
