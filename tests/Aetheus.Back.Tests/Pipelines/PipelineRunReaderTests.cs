// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The run detail re-asks launch warnings only while the run is live, masks every secret-bearing
/// step field, and links the commit to the git graph when the run belongs to a project.</summary>
public sealed class PipelineRunReaderTests
{
    private const string MissingLibrary = "Variable library 'prod' was not found";

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IGitGraphRecorder _gitGraph = Substitute.For<IGitGraphRecorder>();
    private readonly ISecretMaskingService _masking = Substitute.For<ISecretMaskingService>();
    private readonly IPipelineVariableResolver _variables = Substitute.For<IPipelineVariableResolver>();
    private readonly PipelineRunReader _sut;

    public PipelineRunReaderTests()
    {
        _repo.GetRunResultSummariesAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunResultSummaries(null, null, [], []));
        _repo.HydrateLinkedGradeAsync(Arg.Any<PipelineRunDto>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PipelineRunDto>());
        _masking.MaskAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<string>().Replace("s3cr3t", "***", StringComparison.Ordinal));
        _sut = new PipelineRunReader(_repo, _gitGraph, _masking, _variables);
    }

    [Fact]
    public async Task LiveRun_DropsTheWarningOfALibraryCreatedSince()
    {
        GivenRun(PipelineStatus.Running, projectId: null);
        _variables.DropResolvedLibraryWarningsAsync(
                Arg.Any<IReadOnlyCollection<string>>(), null, Arg.Any<CancellationToken>())
            .Returns([]);

        var run = await _sut.GetRunAsync(7, TestContext.Current.CancellationToken);

        Assert.Empty(Assert.IsType<PipelineRunDto>(run).Warnings);
    }

    [Fact]
    public async Task LiveRun_KeepsAWarningStillTrue()
    {
        GivenRun(PipelineStatus.WaitingForApproval, projectId: null);
        _variables.DropResolvedLibraryWarningsAsync(
                Arg.Any<IReadOnlyCollection<string>>(), null, Arg.Any<CancellationToken>())
            .Returns([MissingLibrary]);

        var run = await _sut.GetRunAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal([MissingLibrary], Assert.IsType<PipelineRunDto>(run).Warnings);
    }

    [Fact]
    public async Task FinishedRun_KeepsTheLaunchRecordWithoutAsking()
    {
        GivenRun(PipelineStatus.Success, projectId: null);

        var run = await _sut.GetRunAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal([MissingLibrary], Assert.IsType<PipelineRunDto>(run).Warnings);
        await _variables.DidNotReceive().DropResolvedLibraryWarningsAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutputVariables_AreMaskedAndProjectRunIsLinkedToTheGraph()
    {
        GivenRun(PipelineStatus.Success, projectId: 3,
            outputVariablesJson: "{\"TOKEN\":\"s3cr3t\",\"PLAIN\":\"value\"}");
        List<CommitLinkDto> commits = [new() { Sha = "abc123" }];
        _gitGraph.ResolveRunLinksAsync(3, "abc123", "main", Arg.Any<CancellationToken>())
            .Returns((commits, new List<BranchLinkDto>()));

        var run = Assert.IsType<PipelineRunDto>(await _sut.GetRunAsync(7, TestContext.Current.CancellationToken));

        var step = Assert.Single(run.Steps);
        Assert.Equal("***", step.OutputVariables["TOKEN"]);
        Assert.Equal("value", step.OutputVariables["PLAIN"]);
        Assert.Same(commits, run.Commits);
    }

    private void GivenRun(PipelineStatus status, int? projectId, string? outputVariablesJson = null) =>
        _repo.GetRunDetailAsync(7, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 7,
            PipelineId = 1,
            Status = status,
            CommitHash = "abc123",
            BranchName = "main",
            WarningsJson = $"[\"{MissingLibrary}\"]",
            Pipeline = new Pipeline { Name = "CI", ProjectId = projectId },
            StepRuns =
            [
                new PipelineStepRun
                {
                    Id = 1,
                    StepName = "build",
                    StageName = "stage1",
                    OutputVariablesJson = outputVariablesJson,
                },
            ],
        });
}
