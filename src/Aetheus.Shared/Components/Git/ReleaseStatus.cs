// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Git;

// Lives with the cross-link tiles (ReleaseLinkDto) that carry it, so the Git contracts stay at the
// bottom: Pipelines and Releases both show those tiles, and neither may be reached back from Git.
public enum ReleaseStatus
{
    Detected = 0,
    Building = 1,
    Published = 2,
    Failed = 3,
    RolledBack = 4,
    Promoted = 5,

    /// <summary>Cross-agent deployment: the release artifact was applied on a deployment-capable agent
    /// (binary flip + restart, or container compose up) and the app passed the post-deploy health gate.
    /// Set by the deploy-success closure: a successful <c>type: deploy</c> task flips any linked
    /// release here (via <c>IArtifactService.MarkDeployedAsync</c> from <c>TaskService.CompleteTaskAsync</c>)
    /// and applies the Deployed retention window. No migration - the column is the same <c>int</c> enum.</summary>
    Deployed = 6,

    /// <summary>The release was deployed successfully and was later replaced by a newer deployment.
    /// Keeping this distinct from Published preserves the authoritative V-1 deployment lineage.</summary>
    Superseded = 7,

    /// <summary>Decision D2 (PLAN-002 lot 3): older than the retention window and needed by nothing, the
    /// release lost its artifacts, their files and its runs, but it stays, with its commit, so a complete
    /// pipeline can run again from that commit. Set only by <c>ReleaseRetentionRepository</c>; a new
    /// publication of the same version (what that relaunch produces) makes it Published again.</summary>
    Archived = 8
}
