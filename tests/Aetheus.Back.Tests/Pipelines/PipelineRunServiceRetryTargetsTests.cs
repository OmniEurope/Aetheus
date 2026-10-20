// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A retry replays the run's captured definition, so the servers it may touch are those that
/// definition can reach, resolved in the run's own organization and project; a run with no pipeline or
/// no readable definition has no targets to authorize.</summary>
public sealed class PipelineRunServiceRetryTargetsTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunDefinitionParser _definitions = Substitute.For<IPipelineRunDefinitionParser>();
    private readonly IPipelineRunParameterResolver _parameters = Substitute.For<IPipelineRunParameterResolver>();
    private readonly PipelineRunService _sut;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public PipelineRunServiceRetryTargetsTests()
    {
        _sut = new PipelineRunService(
            _repo, Substitute.For<IPipelineVariableResolver>(), Substitute.For<ILogger<PipelineRunService>>(),
            Substitute.For<IPipelineRunFinalizer>(), _parameters, Substitute.For<IPipelineCheckpointReuseService>(),
            Substitute.For<IPipelineRunPreparationService>(), Substitute.For<IPipelineRunControlService>(),
            Substitute.For<IPipelineTriggerStepCoordinator>(), Substitute.For<IPipelineRunLauncher>(),
            Substitute.For<IPipelineRunScheduler>(), Substitute.For<IPipelineRunPreflightService>(),
            Substitute.For<IPipelineAdvisoryPreflightBuilder>(), Substitute.For<IPipelineRunReader>(), _definitions);
    }

    [Fact]
    public async Task ARunThatIsGone_HasNoTargets()
    {
        _repo.GetPipelineRunWithPipelineAsync(5, Arg.Any<CancellationToken>()).Returns((PipelineRun?)null);

        Assert.Null(await _sut.GetRetryTargetServerIdsAsync(5, _ct));
        _definitions.DidNotReceiveWithAnyArgs().Parse(default!);
    }

    [Fact]
    public async Task ARunWithoutItsPipeline_HasNoTargets()
    {
        _repo.GetPipelineRunWithPipelineAsync(5, Arg.Any<CancellationToken>()).Returns(new PipelineRun { Id = 5, Pipeline = null! });

        Assert.Null(await _sut.GetRetryTargetServerIdsAsync(5, _ct));
        _definitions.DidNotReceiveWithAnyArgs().Parse(default!);
    }

    [Fact]
    public async Task ARunWhoseCapturedDefinitionCannotBeRead_HasNoTargets()
    {
        var run = new PipelineRun { Id = 5, Pipeline = new Pipeline { Id = 2, Name = "p" } };
        _repo.GetPipelineRunWithPipelineAsync(5, Arg.Any<CancellationToken>()).Returns(run);
        _definitions.Parse(run).Returns((PipelineYamlDefinition?)null);

        Assert.Null(await _sut.GetRetryTargetServerIdsAsync(5, _ct));
        await _parameters.DidNotReceiveWithAnyArgs().ResolveCandidateTargetServerIdsAsync(default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheTargets_AreResolvedFromTheCapturedDefinitionInTheRunsOrganizationAndProject()
    {
        var run = new PipelineRun { Id = 5, Pipeline = new Pipeline { Id = 2, Name = "p" } };
        var definition = new PipelineYamlDefinition { Name = "p" };
        IReadOnlyCollection<int> servers = [3, 4];
        _repo.GetPipelineRunWithPipelineAsync(5, Arg.Any<CancellationToken>()).Returns(run);
        _definitions.Parse(run).Returns(definition);
        _repo.GetPipelineOrganizationIdAsync(2, Arg.Any<CancellationToken>()).Returns(9);
        _repo.GetRunProjectIdAsync(5, Arg.Any<CancellationToken>()).Returns(7);
        _parameters.ResolveCandidateTargetServerIdsAsync(definition, 9, 7, Arg.Any<CancellationToken>()).Returns(servers);

        var result = await _sut.GetRetryTargetServerIdsAsync(5, _ct);

        Assert.Equal([3, 4], result);
    }
}
