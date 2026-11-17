// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// The fixes one advisory states for one package, read from the <c>fixed</c> events of its ranges (OSV schema).
/// A fix counts only when it is above the current version, when the ecosystem can order it against that version
/// and when the same advisory does not affect it: a fix that sits inside another range of the advisory is not one.
/// </summary>
public static class CveAdvisoryFixes
{
    /// <summary>The distinct fixed versions above <paramref name="current"/>, in the order the advisory lists them.</summary>
    public static IReadOnlyList<string> FixesAbove(string ecosystem, string current, AdvisoryRanges advisory)
    {
        if (advisory.RangesJson is null || VersionComparators.Compare(ecosystem, current, current) != VersionOrder.Equal) return [];
        var fixes = new List<string>();
        foreach (var fixedVersion in ReadFixedVersions(advisory.RangesJson))
        {
            if (fixes.Contains(fixedVersion, StringComparer.Ordinal)) continue;
            if (VersionComparators.Compare(ecosystem, fixedVersion, current) != VersionOrder.Greater) continue;
            if (VulnerabilityVersionEvaluator.Evaluate(ecosystem, fixedVersion, advisory.RangesJson, advisory.VersionsJson) is not null) continue;
            fixes.Add(fixedVersion);
        }

        return fixes;
    }

    /// <summary>The <c>fixed</c> events of the ranges that are not GIT ones (a commit is not a package version).</summary>
    private static IEnumerable<string> ReadFixedVersions(string rangesJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rangesJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var version in document.RootElement.EnumerateArray().SelectMany(FixedOf))
                yield return version;
        }
    }

    private static IEnumerable<string> FixedOf(JsonElement range)
    {
        if (range.ValueKind != JsonValueKind.Object || IsGit(range)) return [];
        if (!range.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return [];
        return events.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("fixed", out var value) && value.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("fixed").GetString()!)
            .ToList();
    }

    private static bool IsGit(JsonElement range) =>
        range.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("GIT");
}
