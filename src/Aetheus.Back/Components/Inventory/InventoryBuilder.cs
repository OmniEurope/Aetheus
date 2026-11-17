// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;

namespace Aetheus.Back.Components.Inventory;

internal sealed record InventoryItemDraft(
    string Ecosystem, string PackageName, string Version, string PackageUrl, bool IsDirect, string? SourcePath, ProjectInventoryItemKind Kind);

internal sealed record InventoryBuildResult(IReadOnlyList<InventoryItemDraft> Items, ProjectInventoryCoverage Coverage, string? Reason);

/// <summary>
/// Turns what the sources state into the stored inventory: one row per package, version and kind,
/// spelled as the advisories spell them. Nothing known is <see cref="ProjectInventoryCoverage.None"/>,
/// never an empty success.
/// </summary>
internal static class InventoryBuilder
{
    private const int MaxReasonLength = 1000;
    private const int MaxVersionLength = 256;
    private const int MaxNameLength = 512;
    private const int MaxEcosystemLength = 64;
    private const int MaxPackageUrlLength = 1024;
    private const int MaxPathLength = 512;

    public static InventoryBuildResult Build(
        IEnumerable<InventoryEntry> entries, IEnumerable<InventoryGap> gaps, IReadOnlySet<string> directNames, int maxItems)
    {
        var allGaps = gaps.ToList();
        var items = new Dictionary<(string Ecosystem, string Name, string Version, ProjectInventoryItemKind Kind), InventoryItemDraft>();
        foreach (var entry in entries)
        {
            var name = VulnerabilityPackageName.Normalize(entry.Ecosystem, entry.Name);
            var purl = InventoryPurl.Build(entry.Ecosystem, name, entry.Version);
            if (entry.Ecosystem.Length > MaxEcosystemLength || name.Length > MaxNameLength
                || entry.Version.Length > MaxVersionLength || purl.Length > MaxPackageUrlLength)
            {
                allGaps.Add(new InventoryGap(InventoryGapCodes.Oversized));
                continue;
            }

            var key = (entry.Ecosystem, name, entry.Version, entry.Kind);
            var direct = entry.IsDirect || directNames.Contains(entry.Name);
            if (items.TryGetValue(key, out var known))
                items[key] = known with { IsDirect = known.IsDirect || direct };
            else
                items[key] = new InventoryItemDraft(entry.Ecosystem, name, entry.Version, purl, direct, Bounded(entry.SourcePath), entry.Kind);
        }

        var kept = items.Values.ToList();
        if (kept.Count > maxItems)
        {
            kept = [.. kept.Take(maxItems)];
            allGaps.Add(new InventoryGap(InventoryGapCodes.ItemLimit));
        }

        var coverage = kept.Count == 0 ? ProjectInventoryCoverage.None
            : allGaps.Count > 0 ? ProjectInventoryCoverage.Partial
            : ProjectInventoryCoverage.Complete;
        return new InventoryBuildResult(kept, coverage, Reason(allGaps));
    }

    public static InventoryBuildResult Empty(params InventoryGap[] gaps) =>
        new([], ProjectInventoryCoverage.None, Reason(gaps));

    private static string? Reason(IReadOnlyCollection<InventoryGap> gaps)
    {
        if (gaps.Count == 0) return null;
        var text = string.Join(", ", gaps
            .GroupBy(gap => gap.Technology is null ? gap.Code : $"{gap.Code}:{gap.Technology}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}"));
        return text.Length <= MaxReasonLength ? text : text[..text.LastIndexOf(", ", MaxReasonLength, StringComparison.Ordinal)];
    }

    private static string? Bounded(string? path) =>
        path is null || path.Length <= MaxPathLength ? path : path[^MaxPathLength..];
}
