// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>When Dependency-Track is required, an SBOM-producing run stays in gate Error until its
/// upload succeeds. Read outbox state live because it can change after the run ends.</summary>
internal static class DependencyTrackGateVerdict
{
    public static async Task<AnalysisRunGateDto> ApplyAsync(
        AnalysisRunGateDto gate,
        int runId,
        string? scope,
        DependencyTrackOptions options,
        IAnalysisRepository repository,
        CancellationToken ct)
    {
        if (!options.Enabled || !options.Required
            || AnalysisGateScopes.Normalize(scope) == AnalysisGateScopes.Quality)
            return gate;

        var tracking = await repository.GetDependencyTrackGateStateAsync(runId, ct).ConfigureAwait(false);
        if (!tracking.HasSbom)
            return gate;
        var dependencyTrackState = tracking.Statuses.Count == 0
            ? "dependency-track:missing"
            : tracking.Statuses.Any(status =>
                string.Equals(status, DependencyTrackOutboxStatuses.Failed, StringComparison.Ordinal))
                ? "dependency-track:failed"
                : tracking.Statuses.All(status =>
                    string.Equals(status, DependencyTrackOutboxStatuses.Succeeded, StringComparison.Ordinal))
                    ? null
                    : "dependency-track:pending";
        if (dependencyTrackState is null)
            return gate;

        return gate with
        {
            Status = AnalysisGateStatus.Error,
            MissingProducers = [.. gate.MissingProducers, dependencyTrackState]
        };
    }
}
