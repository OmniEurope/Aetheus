// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// BACK-021: a stage selector that still holds a parameter expression (prepared before the parameters
/// are known) used to match no server by name, so the caller was authorized against an empty set.
/// </summary>
public sealed class PipelineRunParameterResolverTargetTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly PipelineRunParameterResolver _sut;

    public PipelineRunParameterResolverTargetTests()
    {
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string?>(2) is null && call.ArgAt<string?>(0) is null
                ? [41, 42]
                : new List<int>());
        _sut = new PipelineRunParameterResolver(
            _repo, Substitute.For<IPipelineTemplateResolver>(), new PipelineDispatchServerResolver(_repo));
    }

    [Theory]
    [InlineData(null, null, "${{ parameters.runner }}")]
    [InlineData("${{ parameters.pool }}", null, null)]
    [InlineData(null, "${{ parameters.env }}", null)]
    public async Task UnresolvedSelector_IsAuthorizedAgainstEveryEligibleServer(
        string? pool, string? environment, string? agent)
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "child",
            Stages = [new PipelineStageDefinition { Name = "Build", Pool = pool, Environment = environment, Agent = agent ?? string.Empty }]
        };

        var targets = await _sut.ResolveCandidateTargetServerIdsAsync(definition, 7, null, TestContext.Current.CancellationToken);

        Assert.Equal([41, 42], targets.Order());
        await _repo.Received(1).FindCandidateTargetServerIdsAsync(
            null, null, null, OsType.Unknown, 7, false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolvedSelector_KeepsItsOwnCandidates()
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "child",
            Stages = [new PipelineStageDefinition { Name = "Build", Agent = "runner-b" }]
        };

        var targets = await _sut.ResolveCandidateTargetServerIdsAsync(definition, 7, null, TestContext.Current.CancellationToken);

        Assert.Empty(targets);
        await _repo.Received(1).FindCandidateTargetServerIdsAsync(
            null, null, "runner-b", OsType.Unknown, 7, false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }
}
