// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Releases;
using NSubstitute;

namespace Aetheus.Back.Tests.Releases;

/// <summary>Only the previous deployment is offered for redeploy, and only when the deploy gate
/// could accept it (sealed grade, retained artifacts, a pipeline taking
/// candidateVersion).</summary>
public sealed class ReleaseRedeployEnricherTests
{
    private readonly IReleaseRepository _repository = Substitute.For<IReleaseRepository>();

    private static ReleaseDto Release(int id, AnalysisGrade? grade = AnalysisGrade.C, bool artifacts = true) => new()
    {
        Id = id,
        ProjectId = 7,
        AssuranceGrade = grade,
        Artifacts = artifacts ? [new ArtifactLinkDto { Id = 1, Name = "ApplicationPayload-artifacts" }] : []
    };

    private async Task<List<ReleaseDto>> EnrichAsync(ReleaseRedeployTarget target, params ReleaseDto[] releases)
    {
        _repository.GetRedeployTargetsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, ReleaseRedeployTarget> { [7] = target });
        return await ReleaseRedeployEnricher.EnrichAsync([.. releases], _repository, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThePreviousDeployment_IsRedeployableByTheLiveDeployPipeline()
    {
        var result = await EnrichAsync(new ReleaseRedeployTarget(2, 40), Release(1), Release(2), Release(3));

        var previous = result.Single(r => r.Id == 2);
        Assert.True(previous.IsPreviousDeployment);
        Assert.Equal(40, previous.RedeployPipelineId);
        Assert.All(result.Where(r => r.Id != 2), r =>
        {
            Assert.False(r.IsPreviousDeployment);
            Assert.Null(r.RedeployPipelineId);
        });
    }

    /// <summary>Offer quick return for the live release, leaving reserve availability validation to
    /// the agent.</summary>
    [Fact]
    public async Task TheLiveRelease_OffersTheQuickReturn_WhenTheProjectHasARevertPipeline()
    {
        var live = Release(3, grade: null, artifacts: false) with { Status = ReleaseStatus.Deployed };
        var result = await EnrichAsync(new ReleaseRedeployTarget(2, 40, RevertPipelineId: 55), Release(2), live);

        Assert.Equal(55, result.Single(r => r.Id == 3).RevertPipelineId);
        Assert.Null(result.Single(r => r.Id == 2).RevertPipelineId);

        var none = await EnrichAsync(new ReleaseRedeployTarget(2, 40), live);
        Assert.Null(Assert.Single(none).RevertPipelineId);
    }

    [Theory]
    [InlineData(false, true, 40)]   // release-fast: no sealed grade, the gate cannot restore its seal
    [InlineData(true, false, 40)]   // payloads purged
    [InlineData(true, true, null)]  // no pipeline that takes a candidateVersion
    public async Task ThePreviousDeployment_IsMarkedButNotOffered_WhenTheGateWouldRefuseIt(
        bool sealedGrade, bool artifacts, int? pipelineId)
    {
        var result = await EnrichAsync(new ReleaseRedeployTarget(2, pipelineId),
            Release(2, sealedGrade ? AnalysisGrade.C : null, artifacts));

        var previous = Assert.Single(result);
        Assert.True(previous.IsPreviousDeployment);
        Assert.Null(previous.RedeployPipelineId);
    }
}
