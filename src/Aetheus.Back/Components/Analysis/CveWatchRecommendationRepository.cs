// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What a recommendation needs of a match beyond its row: its advisory, and the item the inventory holds.</summary>
internal sealed record RecommendationFacts(
    int AdvisoryId, string AdvisoryExternalId, ProjectInventoryItemKind? ItemKind, string? SourcePath, int? ProjectInventoryId = null);

/// <summary>The files of one inventory that state a runtime or SDK, and those that state a base image.</summary>
internal sealed record InventoryRuntimeFiles(IReadOnlyList<string> Runtime, IReadOnlyList<string> Images);

/// <summary>The ranges one advisory states for one package, with the package they belong to.</summary>
internal sealed record AdvisoryPackageRanges(int AdvisoryId, string Ecosystem, string PackageName, AdvisoryRanges Ranges);

/// <summary>Reads, a page at a time and never a row at a time, what the recommendations of the watch are computed from.</summary>
internal sealed class CveWatchRecommendationRepository(AppDbContext db)
{
    public async Task<Dictionary<long, RecommendationFacts>> GetFactsAsync(IReadOnlyCollection<long> matchIds, CancellationToken ct)
    {
        var ids = matchIds.ToArray();
        var rows = await db.VulnerabilityMatches.AsNoTracking()
            .Where(match => ids.Contains(match.Id))
            .Select(match => new
            {
                match.Id,
                match.AdvisoryId,
                AdvisoryExternalId = match.Advisory.ExternalId,
                ItemKind = match.ProjectInventoryItem == null ? null : (ProjectInventoryItemKind?)match.ProjectInventoryItem.Kind,
                SourcePath = match.ProjectInventoryItem == null ? null : match.ProjectInventoryItem.SourcePath,
                ProjectInventoryId = match.ProjectInventoryItem == null ? (int?)null : match.ProjectInventoryItem.ProjectInventoryId
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(
            row => row.Id, row => new RecommendationFacts(row.AdvisoryId, row.AdvisoryExternalId, row.ItemKind, row.SourcePath, row.ProjectInventoryId));
    }

    /// <summary>
    /// For each of these inventories, the files that really state a runtime (<c>global.json</c>) and a base image (a Dockerfile): the places a
    /// runtime pack or an image package is raised from. Origins that are no file (the SBOM, a server) are left out.
    /// </summary>
    public async Task<Dictionary<int, InventoryRuntimeFiles>> GetRuntimeFilesAsync(IReadOnlyCollection<int> inventoryIds, CancellationToken ct)
    {
        var ids = inventoryIds.ToArray();
        if (ids.Length == 0) return [];
        var rows = await db.ProjectInventoryItems.AsNoTracking()
            .Where(item => ids.Contains(item.ProjectInventoryId) && item.SourcePath != null
                && (item.Kind == ProjectInventoryItemKind.Runtime || item.Kind == ProjectInventoryItemKind.BaseImage))
            .Select(item => new RuntimeFileRow(item.ProjectInventoryId, item.Kind, item.SourcePath!))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(row => InventoryOrigins.IsFile(row.Path))
            .GroupBy(row => row.InventoryId)
            .ToDictionary(
                group => group.Key,
                group => new InventoryRuntimeFiles(
                    [.. Paths(group, ProjectInventoryItemKind.Runtime)], [.. Paths(group, ProjectInventoryItemKind.BaseImage)]));
    }

    private sealed record RuntimeFileRow(int InventoryId, ProjectInventoryItemKind Kind, string Path);

    private static IEnumerable<string> Paths(IEnumerable<RuntimeFileRow> rows, ProjectInventoryItemKind kind) =>
        rows.Where(row => row.Kind == kind).Select(row => row.Path.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>The packages of these advisories that carry one of these names. A withdrawn advisory has none.</summary>
    public async Task<IReadOnlyList<AdvisoryPackageRanges>> GetOfAdvisoriesAsync(
        IReadOnlyCollection<int> advisoryIds, IReadOnlyCollection<string> packageNames, CancellationToken ct)
    {
        var advisories = advisoryIds.ToArray();
        var names = packageNames.ToArray();
        return await db.VulnerabilityAffectedPackages.AsNoTracking()
            .Where(package => advisories.Contains(package.AdvisoryId) && names.Contains(package.PackageName) && package.Advisory.WithdrawnAt == null)
            .Select(package => new AdvisoryPackageRanges(
                package.AdvisoryId, package.Ecosystem, package.PackageName, new AdvisoryRanges(package.RangesJson, package.VersionsJson)))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every advisory in force for one package: a version is only safe when none of them affects it.</summary>
    public async Task<IReadOnlyList<AdvisoryRanges>> GetOfPackageAsync(string ecosystem, string packageName, CancellationToken ct) =>
        await db.VulnerabilityAffectedPackages.AsNoTracking()
            .Where(package => package.Ecosystem == ecosystem && package.PackageName == packageName && package.Advisory.WithdrawnAt == null)
            .Select(package => new AdvisoryRanges(package.RangesJson, package.VersionsJson))
            .ToListAsync(ct).ConfigureAwait(false);
}
