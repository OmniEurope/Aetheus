// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Adds the recommendation of PLAN-004 to the rows of the watch. Two ways, one function
/// (<see cref="CveRecommendationCalculator"/>):
/// <list type="bullet">
/// <item>On the list, from the database alone: two queries for the whole page, a third when a row is a runtime or an
/// image whose end-of-life cycle is read (<see cref="CveWatchEndOfLife"/>), no network call, whatever its size.
/// The target is the version the advisory states.</item>
/// <item>On the detail read of one match, the registry of the package is asked for its published versions and the
/// target is the newest one no known advisory affects. When the registry does not answer, the list's answer stands,
/// marked as not verified.</item>
/// </list>
/// A match that is closed, or only to be verified, gets none: there is nothing to do, or nothing proved to do it about.
/// </summary>
internal sealed class CveWatchRecommendationService(
    CveWatchRecommendationRepository repository, CveWatchRegistryVersions registry, CveWatchEndOfLife endOfLife)
{
    public async Task<List<CveWatchMatchDto>> AddFromAdvisoriesAsync(IReadOnlyList<CveWatchMatchDto> rows, CancellationToken ct)
    {
        var open = rows.Where(NeedsRecommendation).ToList();
        if (open.Count == 0) return [.. rows];
        var facts = await repository.GetFactsAsync([.. open.Select(row => row.MatchId)], ct).ConfigureAwait(false);
        var packages = await repository.GetOfAdvisoriesAsync(
            [.. facts.Values.Select(fact => fact.AdvisoryId).Distinct()], [.. open.Select(row => row.PackageName).Distinct(StringComparer.Ordinal)], ct)
            .ConfigureAwait(false);
        var lifecycle = await endOfLife.LoadAsync(open, ct).ConfigureAwait(false);
        var files = await RuntimeFilesAsync(open.Where(row => facts.ContainsKey(row.MatchId)).Select(row => (row, facts[row.MatchId])), ct)
            .ConfigureAwait(false);
        return [.. rows.Select(row => NeedsRecommendation(row) && facts.TryGetValue(row.MatchId, out var fact)
            ? row with { Recommendation = Compute(row, fact, RangesOf(packages, row, fact), null, lifecycle.HasEnded(row), files) }
            : row)];
    }

    public async Task<CveWatchMatchDto> AddFromRegistryAsync(CveWatchMatchDto row, CancellationToken ct)
    {
        if (!NeedsRecommendation(row)) return row;
        var facts = await repository.GetFactsAsync([row.MatchId], ct).ConfigureAwait(false);
        if (!facts.TryGetValue(row.MatchId, out var fact)) return row;
        var packages = await repository.GetOfAdvisoriesAsync([fact.AdvisoryId], [row.PackageName], ct).ConfigureAwait(false);
        var safe = await SafeVersionsAsync(row, fact, ct).ConfigureAwait(false);
        var lifecycle = await endOfLife.LoadAsync([row], ct).ConfigureAwait(false);
        var files = await RuntimeFilesAsync([(row, fact)], ct).ConfigureAwait(false);
        return row with { Recommendation = Compute(row, fact, RangesOf(packages, row, fact), safe, lifecycle.HasEnded(row), files) };
    }

    /// <summary>The safe versions of a dependency in its registry, or null when it has none to ask or it did not answer.</summary>
    private async Task<SafeVersions?> SafeVersionsAsync(CveWatchMatchDto row, RecommendationFacts fact, CancellationToken ct)
    {
        if (KindOf(row, fact) != ProjectInventoryItemKind.Dependency) return null;
        var published = await registry.ListAsync(row.Ecosystem, row.PackageName, ct).ConfigureAwait(false);
        if (published is null) return null;
        var advisories = await repository.GetOfPackageAsync(row.Ecosystem, row.PackageName, ct).ConfigureAwait(false);
        return CveSafeVersionSelector.Select(row.Ecosystem, row.Version, published, [.. advisories]);
    }

    /// <summary>
    /// The files that state a runtime or a base image, for the inventories of the runtimes and images whose own path is an origin (an SBOM
    /// component) and not a file: that is where a runtime pack is raised from. No query when no row needs it.
    /// </summary>
    private async Task<Dictionary<int, InventoryRuntimeFiles>> RuntimeFilesAsync(
        IEnumerable<(CveWatchMatchDto Row, RecommendationFacts Fact)> matches, CancellationToken ct)
    {
        var inventories = matches
            .Where(match => KindOf(match.Row, match.Fact) is ProjectInventoryItemKind.Runtime or ProjectInventoryItemKind.BaseImage
                            && !InventoryOrigins.IsFile(match.Fact.SourcePath) && match.Fact.ProjectInventoryId is not null)
            .Select(match => match.Fact.ProjectInventoryId!.Value)
            .Distinct()
            .ToList();
        return await repository.GetRuntimeFilesAsync(inventories, ct).ConfigureAwait(false);
    }

    private static CveWatchRecommendationDto? Compute(
        CveWatchMatchDto row, RecommendationFacts fact, IReadOnlyList<AdvisoryRanges> ranges, SafeVersions? safe, bool endOfLife,
        IReadOnlyDictionary<int, InventoryRuntimeFiles> runtimeFiles)
    {
        // The end of a cycle names no release to move to: its "fixed" bound is the next cycle, which may itself be out of support.
        var fixes = EndOfLifeCycleResolver.IsEndOfLifeAdvisory(fact.AdvisoryExternalId)
            ? []
            : ranges.SelectMany(range => CveAdvisoryFixes.FixesAbove(row.Ecosystem, row.Version, range)).Distinct(StringComparer.Ordinal).ToList();
        var sources = fact.ProjectInventoryId is { } inventoryId && runtimeFiles.TryGetValue(inventoryId, out var found) ? found : null;
        return CveRecommendationCalculator.Compute(new CveRecommendationInput(
            row.Ecosystem, row.Version, KindOf(row, fact), fact.SourcePath, fixes, safe, endOfLife, sources?.Runtime, sources?.Images));
    }

    private static bool NeedsRecommendation(CveWatchMatchDto row) =>
        row.ClosedAt is null && row.Status == VulnerabilityMatchStatus.Affected;

    private static List<AdvisoryRanges> RangesOf(IReadOnlyList<AdvisoryPackageRanges> packages, CveWatchMatchDto row, RecommendationFacts fact) =>
        [.. packages
            .Where(package => package.AdvisoryId == fact.AdvisoryId && package.PackageName == row.PackageName
                              && string.Equals(package.Ecosystem, row.Ecosystem, StringComparison.Ordinal))
            .Select(package => package.Ranges)];

    /// <summary>What the inventory says the item is; for an item it no longer holds, what its ecosystem says.</summary>
    private static ProjectInventoryItemKind KindOf(CveWatchMatchDto row, RecommendationFacts fact) =>
        fact.ItemKind ?? VersionComparators.FamilyOf(row.Ecosystem) switch
        {
            "Debian" or "Ubuntu" or "Alpine" => ProjectInventoryItemKind.SystemPackage,
            "runtime" => ProjectInventoryItemKind.Runtime,
            "docker" => ProjectInventoryItemKind.BaseImage,
            _ => ProjectInventoryItemKind.Dependency
        };
}
