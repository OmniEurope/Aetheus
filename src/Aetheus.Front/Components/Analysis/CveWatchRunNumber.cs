// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary>How a run of the CVE watch is named and reached (R-21, R-22). Its number is the number of the report the watch wrote;
/// the V keeps it apart from the <c>#</c> numbers of the runs of a pipeline.</summary>
internal static class CveWatchRunNumber
{
    public static string Format(int reportId) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"V-{reportId}");

    public static string Href(int reportId) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"analysis/cve-watch/runs/{reportId}");
}
