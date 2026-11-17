// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-10: the ready stages of one dispatch pass share what the pass already asked the database about runners, and
/// nothing outlives the pass. Real selector and real dispatch resolver over a counted runner reader.
/// </summary>
public sealed class PipelineReadyStageServerPassTests
{
    private const int RunId = 9;
    private const int Affinity = 3;
    private const int OrganizationId = 4;
    private const int ProjectId = 7;

    private readonly IPipelineRunnerTargetReader _targets = Substitute.For<IPipelineRunnerTargetReader>();
    private readonly PipelineReadyStageServerSelector _selector;

    public PipelineReadyStageServerPassTests()
    {
        _selector = new PipelineReadyStageServerSelector(
            _targets, new PipelineDispatchServerResolver(_targets), NullLogger.Instance);
        _targets.IsExternalImportRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(false);
        _targets.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([Affinity]);
        _targets.FindOnlineServerByIdAsync(Affinity, Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = Affinity, Name = "runner", Hostname = "runner" });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ReadyStageServerResolution> ResolveAsync(
        PipelineReadyStageServerSelector.Pass? pass, string stage = "Build", string? pool = null) =>
        _selector.ResolveAsync(
            RunId, stage, new PipelineStageDefinition { Name = stage, Pool = pool }, new Dictionary<string, string>(),
            OrganizationId, false, Affinity, ProjectId, Ct, pass);

    [Fact]
    public async Task TwoStagesOfTheSamePass_AskAboutTheImportTheCandidatesAndTheRunnerOnce()
    {
        var pass = new PipelineReadyStageServerSelector.Pass();

        var first = await ResolveAsync(pass, "Build");
        var second = await ResolveAsync(pass, "Test");

        Assert.Equal(Affinity, first.Server?.Id);
        Assert.Same(first.Server, second.Server);
        await _targets.Received(1).IsExternalImportRunAsync(RunId, Arg.Any<CancellationToken>());
        await _targets.Received(1).FindCandidateTargetServerIdsAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
            Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _targets.Received(1).FindOnlineServerByIdAsync(Affinity, Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnotherPass_AsksAgain()
    {
        await ResolveAsync(new PipelineReadyStageServerSelector.Pass());
        await ResolveAsync(new PipelineReadyStageServerSelector.Pass());
        await ResolveAsync(null);

        await _targets.Received(3).IsExternalImportRunAsync(RunId, Arg.Any<CancellationToken>());
        await _targets.Received(3).FindOnlineServerByIdAsync(Affinity, Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AStageWithAnotherSelector_AsksItsOwnCandidates()
    {
        var pass = new PipelineReadyStageServerSelector.Pass();

        await ResolveAsync(pass, "Build");
        await ResolveAsync(pass, "Deploy", pool: "prod-pool");

        await _targets.Received(2).FindCandidateTargetServerIdsAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
            Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARunnerThatWasNotFound_IsAskedAgainByTheNextStageOfThePass()
    {
        var pass = new PipelineReadyStageServerSelector.Pass();
        _targets.FindOnlineServerByIdAsync(Affinity, Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns((Server?)null, new Server { Id = Affinity, Name = "runner", Hostname = "runner" });

        var first = await ResolveAsync(pass, "Build");
        var second = await ResolveAsync(pass, "Test");

        // The first stage waits for the offline runner; the runner that has come back in between is not hidden.
        Assert.Null(first.Server);
        Assert.True(first.RunnerTemporarilyUnavailable);
        Assert.Equal(Affinity, second.Server?.Id);
    }
}
