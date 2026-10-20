// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// The version series of a release: the <c>major.minor</c> of the <c>VersionPrefix</c> in the code's
/// <c>Directory.Build.props</c>. Release numbers count within a series, so <c>1.4.0</c> follows
/// <c>1.3.16</c> as the first release of the 1.4 series. A project whose code declares no
/// <c>VersionPrefix</c> has the single <see cref="None"/> series.
/// </summary>
public static partial class ReleaseSeries
{
    /// <summary>The series of a project whose code declares no <c>VersionPrefix</c>.</summary>
    public const string None = "";

    public const int MaxLength = 32;

    /// <summary>The largest release number a series accepts, kept below the int range so the
    /// next number can always be incremented.</summary>
    public const int MaxNumber = 1_000_000_000;

    public const string Pattern = "^[0-9]+\\.[0-9]+$";

    [GeneratedRegex(Pattern)]
    private static partial Regex SeriesShape();

    // Same expression as the app-version function: the last <VersionPrefix> of its line.
    [GeneratedRegex("^.*<VersionPrefix>([0-9]+)\\.([0-9]+)(\\.[0-9]+)?</VersionPrefix>")]
    private static partial Regex VersionPrefixLine();

    /// <summary>True for <c>major.minor</c> within <see cref="MaxLength"/> characters.</summary>
    public static bool IsValid(string? series) =>
        series is { Length: > 0 and <= MaxLength } && SeriesShape().IsMatch(series);

    /// <summary>
    /// The series a <c>Directory.Build.props</c> declares, under the app-version function's rule:
    /// exactly one line holding <c>&lt;VersionPrefix&gt;</c>, shaped <c>major.minor[.patch]</c>.
    /// Anything else is no series, <see cref="None"/>.
    /// </summary>
    public static string FromVersionProps(string? props)
    {
        if (string.IsNullOrEmpty(props)) return None;
        var lines = props.Split('\n')
            .Where(line => line.Contains("<VersionPrefix>", StringComparison.Ordinal))
            .ToList();
        if (lines.Count != 1) return None;
        var match = VersionPrefixLine().Match(lines[0]);
        if (!match.Success) return None;
        var series = $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        return IsValid(series) ? series : None;
    }
}
