// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Shared.Maven;

/// <summary>
/// Maven snapshot naming: a snapshot version ends in <c>-SNAPSHOT</c>; each deployed build of it is
/// named <c>{base}-{yyyyMMdd.HHmmss}-{buildNumber}</c>, e.g. <c>1.0-20261017.120000-3</c> for
/// <c>1.0-SNAPSHOT</c>.
/// </summary>
public static partial class MavenSnapshot
{
    public const string Suffix = "-SNAPSHOT";

    [GeneratedRegex(@"^(?<base>.+)-(?<timestamp>\d{8}\.\d{6})-(?<build>[1-9]\d{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampedPattern();

    public static bool IsSnapshot(string version)
        => version.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) || TryParseTimestamped(version, out _);

    /// <summary>Reads a timestamped build such as <c>1.0-20261017.120000-3</c>.</summary>
    public static bool TryParseTimestamped(string value, out SnapshotBuild build)
    {
        var match = TimestampedPattern().Match(value);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups["timestamp"].Value, "yyyyMMdd.HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
        {
            build = default;
            return false;
        }

        build = new SnapshotBuild(
            match.Groups["base"].Value,
            match.Groups["timestamp"].Value,
            int.Parse(match.Groups["build"].Value, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>The <c>-SNAPSHOT</c> version a timestamped build belongs to.</summary>
    public static string BaseVersion(string version)
        => TryParseTimestamped(version, out var build) ? build.Base + Suffix : version;
}

/// <summary>One deployed build of a snapshot: <c>{Base}-{Timestamp}-{BuildNumber}</c>.</summary>
public readonly record struct SnapshotBuild(string Base, string Timestamp, int BuildNumber)
{
    public string Value => $"{Base}-{Timestamp}-{BuildNumber.ToString(CultureInfo.InvariantCulture)}";
}
