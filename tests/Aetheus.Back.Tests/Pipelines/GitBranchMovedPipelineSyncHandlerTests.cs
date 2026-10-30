// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Notifications;
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
    private readonly IUserNotificationService _userNotifications = Substitute.For<IUserNotificationService>();
    private readonly GitBranchMovedPipelineSyncHandler _sut;

    public GitBranchMovedPipelineSyncHandlerTests() =>
        _sut = new GitBranchMovedPipelineSyncHandler(new PipelineDefinitionSynchronizer(
            _pipelineService, _cli,
            new PipelineRunNotificationPublisher(Substitute.For<IPipelineRepository>(), _userNotifications),
            NullLogger<PipelineDefinitionSynchronizer>.Instance));

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

    [Fact]
    public async Task Handle_ASyncThatFails_IsNotifiedWithRepositoryRefCommitAndMessage()
    {
        GivenDefinition("aetheus-demo.yaml", Yaml);
        _pipelineService.UpsertPipelineFromYamlAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns<PipelineDto>(_ => throw new ConflictException("no resolvable owner"));

        await _sut.HandleAsync(Moved("main"), TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            "pipeline.launch-refused",
            Arg.Is<string>(payload => payload.Contains("\"PipelineName\":\"Repo\"")
                && payload.Contains("\"ProjectId\":7")
                && payload.Contains("push:main@0123456789ab")
                && payload.Contains("no resolvable owner")),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Handle_AGitReadThatFails_IsNotifiedToo()
    {
        _cli.GetTreeAsync(DiskPath, Commit, ".pipeline", Arg.Any<CancellationToken>())
            .Returns<List<GitLightTreeEntryDto>>(_ => throw new InvalidOperationException("git worktree busy"));

        await _sut.HandleAsync(Moved("main"), TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            "pipeline.launch-refused", Arg.Is<string>(payload => payload.Contains("git worktree busy")),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Handle_ANotificationThatFails_IsLoggedAndNeverRethrown()
    {
        GivenDefinition("aetheus-demo.yaml", Yaml);
        _pipelineService.UpsertPipelineFromYamlAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns<PipelineDto>(_ => throw new ConflictException("no resolvable owner"));
        _userNotifications.RecordProjectEventAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns<int>(_ => throw new InvalidOperationException("inbox unavailable"));

        var exception = await Record.ExceptionAsync(() =>
            _sut.HandleAsync(Moved("main"), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Handle_ACancelledSync_IsNotSwallowedNorNotified()
    {
        _cli.GetTreeAsync(DiskPath, Commit, ".pipeline", Arg.Any<CancellationToken>())
            .Returns<List<GitLightTreeEntryDto>>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.HandleAsync(Moved("main"), TestContext.Current.CancellationToken));

        await _userNotifications.DidNotReceiveWithAnyArgs().RecordProjectEventAsync(default!, default!, TestContext.Current.CancellationToken);
    }
}
