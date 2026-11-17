// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Analysis;

public interface ICveWatchQueryService
{
    /// <summary>
    /// The matches of the vulnerability watch of one organization, a page at a time, the exploited first then
    /// the worst. <paramref name="accessibleProjectIds"/> is the caller's read scope: null for everything of the
    /// organization (a site Admin), otherwise only these projects. Each open match carries the recommendation the
    /// advisory allows, computed from the database for the whole page and with no network call.
    /// </summary>
    Task<PaginatedResult<CveWatchMatchDto>> GetPageAsync(
        int organizationId, IReadOnlyCollection<int>? accessibleProjectIds, int? projectId, bool includeClosed,
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// One match with its recommendation checked against the registry of the package: the target version is
    /// the newest one no known advisory affects. <paramref name="organizationIds"/> is null for a site Admin,
    /// otherwise the organizations of the caller; null is returned for a match the caller may not read.
    /// </summary>
    Task<CveWatchMatchDto?> GetDetailAsync(
        long matchId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default);

    /// <summary>The detail (as <see cref="GetDetailAsync"/>) of the match an analysis finding was written for: the open one first, else the latest. Null when none is readable by the caller.</summary>
    Task<CveWatchMatchDto?> GetDetailByFindingAsync(
        int findingId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default);
}

internal sealed class CveWatchQueryService(
    CveWatchQueryRepository repository,
    CveWatchRecommendationService recommendations) : ICveWatchQueryService
{
    public async Task<PaginatedResult<CveWatchMatchDto>> GetPageAsync(
        int organizationId, IReadOnlyCollection<int>? accessibleProjectIds, int? projectId, bool includeClosed,
        int page, int pageSize, CancellationToken ct = default)
    {
        var found = await repository.GetPageAsync(
            organizationId, accessibleProjectIds, projectId, includeClosed, Math.Max(1, page), PaginationDefaults.Clamp(pageSize), ct)
            .ConfigureAwait(false);
        return found with { Items = await recommendations.AddFromAdvisoriesAsync(found.Items, ct).ConfigureAwait(false) };
    }

    public async Task<CveWatchMatchDto?> GetDetailAsync(
        long matchId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default)
    {
        var row = await repository.GetOneAsync(matchId, organizationIds, accessibleProjectIds, ct).ConfigureAwait(false);
        return row is null ? null : await recommendations.AddFromRegistryAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<CveWatchMatchDto?> GetDetailByFindingAsync(
        int findingId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default)
    {
        var row = await repository.GetOneByFindingAsync(findingId, organizationIds, accessibleProjectIds, ct).ConfigureAwait(false);
        return row is null ? null : await recommendations.AddFromRegistryAsync(row, ct).ConfigureAwait(false);
    }
}
