// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// What a branch the server moved itself means for pipelines: the stored definitions follow the
/// branch exactly as they do for a push, and nothing is triggered. The push counterpart is
/// <see cref="GitPushPipelineTriggerHandlerTests"/>.
/// </summary>
public sealed class GitBranchMovedPipelineSyncHandlerTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string DiskPath = "/srv/git/repo.git";
    private const string Yaml = "name: aetheus-demo\ntrigger: schedule\nsource_branch: main\nstages: []";

    private readonly IPipelineService _pipelineService = Substitute.For<IPipelineService>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly GitBranchMovedPipelineSyncHandler _sut;

    public GitBranchMovedPipelineSyncHandlerTests() =>
        _sut = new GitBranchMovedPipelineSyncHandler(new PipelineDefinitionSynchronizer(
            _pipelineService, _cli, NullLogger<PipelineDefinitionSynchronizer>.Instance));

    private static GitBranchMovedEvent Moved(string branch, string? defaultBranch = "main") =>
        new(7, 3, "repo", "Repo", defaultBranch, DiskPath,
            [new GitRefUpdate($"refs/heads/{branch}", Commit, "fedcba9876543210fedcba9876543210fedcba98")]);

    private void GivenDefinition(string fileName, string content)
    {
        _cli.GetTreeAsync(DiskPath, Commit, ".pipeline", Arg.Any<CancellationToken>())
            .Returns([new GitLightTreeEntryDto { Name = fileName, Type = GitTreeEntryType.Blob }]);
        _cli.GetBlobAsync(DiskPath, Commit, $".pipeline/{fileName}", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = $".pipeline/{fileName}", Content = content });
    }

    [Fact]
    public async Task Handle_SynchronizesTheDefinitionsOfTheMovedBranchTip_AndTriggersNothing()
    {
        GivenDefinition("aetheus-demo.yaml", Yaml);

        await _sut.HandleAsync(Moved("main"), TestContext.Current.CancellationToken);

        await _pipelineService.Received(1).UpsertPipelineFromYamlAsync(
            "aetheus-demo", Yaml, 7, "schedule", Arg.Any<CancellationToken>(), "main", "main", 3);
        // Looking pipelines up to trigger them is what a push does and a moved branch must not.
        await _pipelineService.DidNotReceiveWithAnyArgs().GetWebhookTriggeredPipelinesForProjectAsync(default, TestContext.Current.CancellationToken);
        await _pipelineService.DidNotReceiveWithAnyArgs().HasActiveRunAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Handle_PassesTheMovedBranchAndTheRepositoryDefaultBranchSeparately()
    {
        GivenDefinition("aetheus-demo.yaml", Yaml);

        await _sut.HandleAsync(Moved("release", defaultBranch: "develop"), TestContext.Current.CancellationToken);

        await _pipelineService.Received(1).UpsertPipelineFromYamlAsync(
            "aetheus-demo", Yaml, 7, "schedule", Arg.Any<CancellationToken>(), "release", "develop", 3);
    }

    [Fact]
    public async Task Handle_BranchWithoutDefinitions_UpsertsNothing()
    {
        _cli.GetTreeAsync(DiskPath, Commit, ".pipeline", Arg.Any<CancellationToken>()).Returns([]);

        await _sut.HandleAsync(Moved("main"), TestContext.Current.CancellationToken);

        await _pipelineService.DidNotReceiveWithAnyArgs().UpsertPipelineFromYamlAsync(
            default!, default!, default, default!, TestContext.Current.CancellationToken, default, default, default);
    }

    [Fact]
    public async Task Handle_ARefusedUpsert_DoesNotFailTheMoveThatAlreadyHappened()
    {
        GivenDefinition("aetheus-demo.yaml", Yaml);
        _pipelineService.UpsertPipelineFromYamlAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns<PipelineDto>(_ => throw new ConflictException("no resolvable owner"));

        var exception = await Record.ExceptionAsync(() =>
            _sut.HandleAsync(Moved("main"), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }
}
