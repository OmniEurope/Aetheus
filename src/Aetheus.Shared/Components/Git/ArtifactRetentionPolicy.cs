// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Git;

// Lives with the cross-link tiles (ArtifactLinkDto) that carry it, so the Git contracts stay at the
// bottom: Pipelines and Releases both show those tiles, and neither may be reached back from Git.
public enum ArtifactRetentionPolicy
{
    Build = 0,
    Deployed = 1,
    Released = 2
}
