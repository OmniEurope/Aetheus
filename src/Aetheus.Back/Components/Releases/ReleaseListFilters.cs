// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Components.Releases;

/// <summary> What the releases list's header filters need beyond the generic map. The source
/// pipeline the list shows is the root of the release run's trigger chain (read exactly as <see
/// cref="ReleaseSourcePipelineEnricher"/> shows it), so its filter and its checkable values are
/// resolved over the releases of the caller's scope. </summary>
internal sealed class ReleaseListFilters(IReleaseRepository repo, IPipelineRepository? pipelineRepo)
{
    /// <summary>
    /// Splits the header filters into the stored columns (left to the query) and the source pipeline,
    /// resolved to the ids of the matching releases of the scope. Null and null when the request carries
    /// no filter, so a plain page reads exactly as before.
    /// </summary>
    public async Task<(List<GridFilter>? Stored, HashSet<int>? ReleaseIds)> ResolveAsync(
        List<GridFilter>? filters, int? projectId, List<int>? accessibleIds, int? serverId, CancellationToken ct)
    {
        if (filters is null || filters.Count == 0)
            return (null, null);

        var stored = filters.Where(filter => !ReleaseListQuery.IsResolvedInMemory(filter)).ToList();
        HashSet<int>? releaseIds = null;
        List<(int ReleaseId, string? PipelineName)>? sources = null;
        foreach (var filter in filters.Where(ReleaseListQuery.IsResolvedInMemory))
        {
            sources ??= await SourcesAsync(
                await repo.GetReleaseFilterFactsAsync(projectId, accessibleIds, serverId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            var matching = ReleaseListQuery.WithSourcePipelineAmong(filter, sources);
            releaseIds = releaseIds is null ? matching : [.. releaseIds.Where(matching.Contains)];
        }

        return (stored.Count == 0 ? null : stored, releaseIds);
    }

    /// <summary>The project and source pipeline names across the releases of the scope.</summary>
    public async Task<ReleaseFilterValuesDto> ValuesAsync(
        int? projectId, List<int>? accessibleIds, int? serverId, CancellationToken ct)
    {
        var facts = await repo.GetReleaseFilterFactsAsync(projectId, accessibleIds, serverId, ct).ConfigureAwait(false);
        var sources = await SourcesAsync(facts, ct).ConfigureAwait(false);
        return new ReleaseFilterValuesDto
        {
            ProjectNames = Distinct(facts.Select(fact => fact.ProjectName)),
            SourcePipelineNames = Distinct(sources.Select(source => source.PipelineName))
        };
    }

    /// <summary>Each release with the name of its source pipeline, null when it has none.</summary>
    private async Task<List<(int ReleaseId, string? PipelineName)>> SourcesAsync(List<ReleaseFilterFact> facts, CancellationToken ct)
    {
        var runIds = facts.Where(fact => fact.PipelineRunId.HasValue).Select(fact => fact.PipelineRunId!.Value).Distinct().ToList();
        Dictionary<int, PipelineRunRootReference> roots = pipelineRepo is null || runIds.Count == 0
            ? []
            : await pipelineRepo.GetRootRunReferencesAsync(runIds, ct).ConfigureAwait(false) ?? [];
        return [.. facts.Select(fact => (fact.Id,
            fact.PipelineRunId is { } runId && roots.TryGetValue(runId, out var root) ? root.PipelineName : null))];
    }

    private static List<string> Distinct(IEnumerable<string?> values) => [.. values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)];
}
