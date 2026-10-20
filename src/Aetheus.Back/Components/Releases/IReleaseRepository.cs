// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Releases;

public interface IReleaseRepository
{
    /// <summary>Lists releases within the supplied scope.</summary> <param
    /// name="deployableOnly">Restricts results to releases with retained artifacts, regardless of
    /// status.</param>
    Task<(List<Release> Items, int TotalCount)> GetReleasesPagedAsync(
        string? search, int? projectId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true, bool deployableOnly = false,
        IReadOnlyList<GridFilter>? columnFilters = null, IReadOnlyCollection<int>? releaseIds = null);

    /// <summary>The id, project and run of every release of a scope (a server's when <paramref
    /// name="serverId"/> is set), for the list's checkable filters.</summary>
    Task<List<ReleaseFilterFact>> GetReleaseFilterFactsAsync(
        int? projectId, List<int>? accessibleIds, int? serverId, CancellationToken ct = default);

    Task<List<Release>> GetProjectReleasesAsync(int projectId, CancellationToken ct = default);

    Task<Release?> FindReleaseAsync(int id, CancellationToken ct = default);

    Task<Release?> FindByVersionAsync(int projectId, string version, CancellationToken ct = default);

    Task<List<Release>> GetDeployedProjectReleasesAsync(int projectId, CancellationToken ct = default);

    /// <summary>Per project, returns the release deployed before the live release and the pipeline
    /// that deployed the live one with candidateVersion.</summary>
    Task<Dictionary<int, ReleaseRedeployTarget>> GetRedeployTargetsAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default);

    Task AddReleaseAsync(Release release, CancellationToken ct = default);

    Task<Release?> FindByPipelineRunIdAsync(int pipelineRunId, CancellationToken ct = default);

    Task<Release?> FindPreviousPublishedWithArtifactAsync(Release release, CancellationToken ct = default);

    Task AddRollbackAsync(ReleaseRollback rollback, CancellationToken ct = default);

    Task<ReleaseRollback?> FindRollbackByPipelineRunIdAsync(int pipelineRunId, CancellationToken ct = default);

    Task<ReleaseRollback?> FindRollbackAsync(int id, CancellationToken ct = default);

    Task<List<Release>> GetByPipelineRunIdAsync(int pipelineRunId, CancellationToken ct = default);

    Task<int> GetMaxBuildNumberAsync(int projectId, CancellationToken ct = default);

    /// <summary>Releases of every project a server is involved in; see the implementation for why it
    /// lives here rather than in Servers.</summary>
    Task<List<Release>> GetReleasesForServerAsync(int serverId, CancellationToken ct = default);

    /// <summary>One page of a server's releases, newest first, with the total count.</summary>
    Task<(List<Release> Items, int TotalCount)> GetReleasesForServerPagedAsync(
        int serverId, int page, int pageSize, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true,
        IReadOnlyList<GridFilter>? columnFilters = null, IReadOnlyCollection<int>? releaseIds = null);

    Task SaveChangesAsync(CancellationToken ct = default);
}
