// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Releases;

/// <summary>The release deployed before the live one, the pipeline that deployed the live one, and
/// the project's "Revenir à N-1" pipeline (one extending <c>host-bluegreen-revert</c>) when it has one.</summary>
public sealed record ReleaseRedeployTarget(int PreviousReleaseId, int? DeployPipelineId, int? RevertPipelineId = null);

/// <summary> Marks the release production ran just before the live one, and offers to redeploy it
/// only when that can actually pass the deploy gate: a sealed grade (the gate restores its
/// assurance seal, which a release-fast release never had) and retained artifacts. The gate itself
/// stays the authority; this only keeps the grid from offering what it would refuse. </summary>
internal static class ReleaseRedeployEnricher
{
    public static async Task<List<ReleaseDto>> EnrichAsync(
        List<ReleaseDto> releases, IReleaseRepository repository, CancellationToken ct)
    {
        if (releases.Count == 0) return releases;
        var targets = await repository.GetRedeployTargetsAsync(
            releases.Select(release => release.ProjectId).Distinct().ToList(), ct).ConfigureAwait(false);
        if (targets is null || targets.Count == 0) return releases;

        return releases.Select(release =>
        {
            if (!targets.TryGetValue(release.ProjectId, out var target)) return release;
            // Offer quick return when a previous deployment and revert pipeline exist. The agent
            // validates reserve readiness because the grid cannot know whether that colour still
            // runs.
            if (release.Status == ReleaseStatus.Deployed)
                return target.RevertPipelineId is { } revert ? release with { RevertPipelineId = revert } : release;
            if (target.PreviousReleaseId != release.Id)
                return release;
            var redeployable = target.DeployPipelineId is not null
                && release.AssuranceGrade is not null
                && release.Artifacts.Count > 0;
            return release with
            {
                IsPreviousDeployment = true,
                RedeployPipelineId = redeployable ? target.DeployPipelineId : null
            };
        }).ToList();
    }
}
