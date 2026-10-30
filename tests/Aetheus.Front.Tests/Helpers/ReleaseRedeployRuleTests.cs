// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests;

/// <summary>R-72: which releases the screens offer to deploy again, and which of those are enabled.</summary>
public class ReleaseRedeployRuleTests
{
    private static ReleaseDto Release(ReleaseStatus status, bool withArtifact) => new()
    {
        Status = status,
        Artifacts = withArtifact ? [new ArtifactLinkDto { Id = 1, Name = "payload" }] : []
    };

    [Theory]
    [InlineData(ReleaseStatus.Published)]
    [InlineData(ReleaseStatus.Promoted)]
    [InlineData(ReleaseStatus.Deployed)]
    [InlineData(ReleaseStatus.Superseded)]
    [InlineData(ReleaseStatus.RolledBack)]
    public void APublishedReleaseWithAKeptArtifact_CanBeRedeployed_WhateverTheLiveRelease(ReleaseStatus status)
    {
        Assert.True(ReleaseRedeploy.IsOffered(status));
        Assert.True(ReleaseRedeploy.CanRedeploy(Release(status, withArtifact: true)));
    }

    [Theory]
    [InlineData(ReleaseStatus.Published)]
    [InlineData(ReleaseStatus.Superseded)]
    [InlineData(ReleaseStatus.Archived)]
    public void AReleaseWithoutAKeptArtifact_CannotBeRedeployed_ButIsStillShown(ReleaseStatus status)
    {
        Assert.True(ReleaseRedeploy.IsOffered(status));
        Assert.False(ReleaseRedeploy.CanRedeploy(Release(status, withArtifact: false)));
    }

    [Theory]
    [InlineData(ReleaseStatus.Detected)]
    [InlineData(ReleaseStatus.Building)]
    [InlineData(ReleaseStatus.Failed)]
    public void AReleaseThatNeverShippedAPayload_IsNotOfferedAtAll(ReleaseStatus status)
    {
        Assert.False(ReleaseRedeploy.IsOffered(status));
        Assert.False(ReleaseRedeploy.CanRedeploy(Release(status, withArtifact: true)));
    }
}
