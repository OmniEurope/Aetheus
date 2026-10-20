// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Downstream chaining + cycle guard (A→B→A). The handler appends the current pipeline to an
/// UPSTREAM_CHAIN lineage and refuses to trigger a target already in that chain or past the depth cap.
/// </summary>
public sealed class PipelineRunCompletedDownstreamHandlerTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunService _runService = Substitute.For<IPipelineRunService>();
    private readonly PipelineRunCompletedDownstreamHandler _sut;

    public PipelineRunCompletedDownstreamHandlerTests()
        => _sut = new PipelineRunCompletedDownstreamHandler(_repo, _runService, NullLogger<PipelineRunCompletedDownstreamHandler>.Instance);

    private const string Yaml = "name: A\non_success:\n  - pipeline: B\n";

    private void Arrange(int runPipelineId, string additionalVarsJson, Pipeline? targetB,
        string? commitHash = null, string? branchName = null)
    {
        var run = new PipelineRun
        {
            Id = 99,
            PipelineId = runPipelineId,
            YamlSnapshot = Yaml,
            AdditionalVariablesJson = additionalVarsJson,
            CommitHash = commitHash,
            BranchName = branchName,
            Pipeline = new Pipeline { Id = runPipelineId, Name = "A", YamlDefinition = Yaml }
        };
        _repo.GetPipelineRunWithPipelineAsync(99, Arg.Any<CancellationToken>()).Returns(run);
        _repo.GetPipelineProjectIdAsync(run.Pipeline, Arg.Any<CancellationToken>()).Returns(7);
        _repo.FindPipelineByNameAndProjectAsync("B", 7, Arg.Any<CancellationToken>()).Returns(targetB);
    }

    [Fact]
    public async Task Triggers_downstream_with_chain_when_no_cycle()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).TriggerChainedRunAsync(2,
            Arg.Is<Dictionary<string, string>>(v => v["UPSTREAM_CHAIN"] == "1"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PipelineStatus.Partial)]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    public async Task Does_not_trigger_downstream_for_a_non_success(PipelineStatus status)
    {
        // Partial outcomes do not trigger success-only downstream pipelines.
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, status), ct: TestContext.Current.CancellationToken);

        await _runService.DidNotReceive().TriggerChainedRunAsync(
            Arg.Any<int>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Labels_the_downstream_run_with_the_run_that_started_it()
    {
        // Without this the child reads the static trigger of its own definition.
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).TriggerChainedRunAsync(2,
            Arg.Is<Dictionary<string, string>>(v => v["BUILD_TRIGGEREDBY"] == "on_success:A#99"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Triggers_downstream_with_same_source_commit()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        Arrange(1, "{}", new Pipeline { Id = 2, Name = "B" }, commit);

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).TriggerChainedRunAsync(2,
            Arg.Is<Dictionary<string, string>>(v => v["AETHEUS_SOURCE_COMMIT"] == commit),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Triggers_downstream_with_same_source_branch()
    {
        Arrange(1, "{}", new Pipeline { Id = 2, Name = "B" }, branchName: "release/1");

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).TriggerChainedRunAsync(2,
            Arg.Is<Dictionary<string, string>>(v => v["AETHEUS_RUN_BRANCH"] == "release/1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Appends_current_pipeline_to_existing_ancestor_chain()
    {
        // G4 round-trip: an upstream run that already carries a lineage ("5") must forward the
        // accumulated chain "5,1" - this is the exact string PipelineRunService persists into the
        // downstream run's AdditionalVariablesJson and that ReadAncestorChain reads back on the next
        // hop, so a deeper A→B→A cycle (not just immediate A→A) stays detectable.
        Arrange(runPipelineId: 1, additionalVarsJson: "{\"UPSTREAM_CHAIN\":\"5\"}", targetB: new Pipeline { Id = 2, Name = "B" });

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).TriggerChainedRunAsync(2,
            Arg.Is<Dictionary<string, string>>(v => v["UPSTREAM_CHAIN"] == "5,1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refuses_cycle_when_target_already_in_chain()
    {
        // Chain already contains B(2); A(1) completing would re-trigger B which loops back to A → refuse.
        Arrange(runPipelineId: 1, additionalVarsJson: "{\"UPSTREAM_CHAIN\":\"2,1\"}", targetB: new Pipeline { Id = 2, Name = "B" });

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.DidNotReceive().TriggerChainedRunAsync(Arg.Any<int>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_trigger_on_failed_run()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Failed), ct: TestContext.Current.CancellationToken);

        await _runService.DidNotReceive().TriggerChainedRunAsync(Arg.Any<int>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }
}
