// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Analysis;

/// <summary>The CVE watch tab of the quality page (R-15) and the page of one watch run (R-22).</summary>
public interface ICveWatchOverviewService
{
    /// <summary>The advisories, the exploited first then the worst, each with its matches in the projects the caller may read.</summary>
    Task<PaginatedResult<CveWatchAdvisoryGroupDto>> GetAdvisoriesAsync(
        CveWatchScope scope, CveWatchAdvisoryFilter filter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>The figures of the tiles, the projects the watch cannot vouch for and how fresh its sources are.</summary>
    Task<CveWatchOverviewDto> GetOverviewAsync(CveWatchScope scope, CancellationToken ct = default);

    /// <summary>One watch run, null when it does not exist or the caller may not read its project.</summary>
    Task<CveWatchRunDto?> GetRunAsync(int reportId, CveWatchScope scope, CancellationToken ct = default);

    /// <summary>What the watch says of one project (open CVE by severity, inventories, last pass, sources), null when the project does not exist or the caller may not read it.</summary>
    Task<CveWatchProjectDto?> GetProjectAsync(int projectId, CveWatchScope scope, CancellationToken ct = default);
}

internal sealed class CveWatchOverviewService(
    CveWatchOverviewRepository overview,
    CveWatchRunRepository runs,
    CveWatchProjectRepository projects,
    IVulnerabilityFeedRepository feeds,
    IOptions<VulnerabilityWatchOptions> options,
    TimeProvider timeProvider) : ICveWatchOverviewService
{
    public Task<PaginatedResult<CveWatchAdvisoryGroupDto>> GetAdvisoriesAsync(
        CveWatchScope scope, CveWatchAdvisoryFilter filter, int page, int pageSize, CancellationToken ct = default) =>
        overview.GetAdvisoryPageAsync(scope, filter, Math.Max(1, page), PaginationDefaults.Clamp(pageSize), ct);

    public async Task<CveWatchOverviewDto> GetOverviewAsync(CveWatchScope scope, CancellationToken ct = default)
    {
        var counts = await overview.CountOpenAsync(scope, ct).ConfigureAwait(false);
        return new CveWatchOverviewDto
        {
            CriticalCount = counts.Critical,
            ExploitedCount = counts.Exploited,
            WithoutFixCount = counts.WithoutFix,
            UncoveredProjects = await overview.GetUncoveredProjectsAsync(scope, ct).ConfigureAwait(false),
            Sources = await GetSourcesAsync(ct).ConfigureAwait(false)
        };
    }

    public async Task<CveWatchRunDto?> GetRunAsync(int reportId, CveWatchScope scope, CancellationToken ct = default)
    {
        var run = await runs.GetAsync(reportId, scope.OrganizationIds, scope.ProjectIds, ct).ConfigureAwait(false);
        return run is null ? null : run with { Sources = await GetSourcesAsync(ct).ConfigureAwait(false) };
    }

    public async Task<CveWatchProjectDto?> GetProjectAsync(int projectId, CveWatchScope scope, CancellationToken ct = default)
    {
        var project = await projects.GetAsync(projectId, scope, ct).ConfigureAwait(false);
        return project is null ? null : project with { Sources = await GetSourcesAsync(ct).ConfigureAwait(false) };
    }

    /// <summary>Every source the configuration enables, those never loaded included: a source that never succeeded is stale.</summary>
    private async Task<List<CveWatchSourceDto>> GetSourcesAsync(CancellationToken ct)
    {
        var configured = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var threshold = TimeSpan.FromHours(configured.StaleAfterHours);
        var states = (await feeds.GetStatesAsync(ct).ConfigureAwait(false)).ToDictionary(state => state.Source, StringComparer.Ordinal);
        var keys = VulnerabilityFeedFreshness.ExpectedSources(configured)
            .Concat(states.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return
        [
            .. keys.Select(key =>
            {
                states.TryGetValue(key, out var state);
                return new CveWatchSourceDto
                {
                    Source = key,
                    LastSuccessAt = state?.LastSuccessAt,
                    LastAttemptAt = state?.LastAttemptAt,
                    LastErrorAt = state?.LastErrorAt,
                    IsStale = VulnerabilityFeedFreshness.IsStale(state?.LastSuccessAt, now, threshold)
                };
            })
        ];
    }
}
