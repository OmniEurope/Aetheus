// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// What a git push means for pipelines, now that Git only announces it.
///
/// These assertions came with the behaviour when it moved out of GitSmartHttpService: the same
/// coverage, exercised where the decision now lives. Losing them in the move would have been the
/// worst outcome of the refactor - the trigger path is how every webhook-driven run starts.
/// </summary>
public sealed class GitPushPipelineTriggerHandlerTests
{
    private readonly IPipelineService _pipelineService = Substitute.For<IPipelineService>();
    private readonly IPipelineRunService _runService = Substitute.For<IPipelineRunService>();
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly GitPushPipelineTriggerHandler _sut;

    public GitPushPipelineTriggerHandlerTests() =>
        _sut = new GitPushPipelineTriggerHandler(
            _pipelineService, _runService, _pipelineRepo, _cli,
            new PipelineDefinitionSynchronizer(_pipelineService, _cli, NullLogger<PipelineDefinitionSynchronizer>.Instance),
            NullLogger<GitPushPipelineTriggerHandler>.Instance);

    private static GitPushProcessedEvent Push(string reference, string commit, string defaultBranch = "main") =>
        new(7, 1, "repo", "Repo", defaultBranch, "/srv/git/repo.git", [new GitRefUpdate(reference, commit)]);

    private static GitPushProcessedEvent Push(string reference, string oldCommit, string commit, string defaultBranch) =>
        new(7, 1, "repo", "Repo", defaultBranch, "/srv/git/repo.git", [new GitRefUpdate(reference, commit, oldCommit)]);

    /// <summary>
    /// Definitions are synchronised from the pushed commit BEFORE anything is triggered, so a push
    /// that edits a pipeline runs the edited one - and both use the commit the push actually
    /// accepted, not the branch tip at some later moment.
    /// </summary>
    [Fact]
    public async Task Handle_SyncsDefinitionsFromTheAcceptedCommitThenTriggers()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string yaml = "name: release\ntrigger: webhook\nbranches:\n  - main\nstages: []";
        _cli.GetTreeAsync("/srv/git/repo.git", commit, ".pipeline", Arg.Any<CancellationToken>())
            .Returns([new GitLightTreeEntryDto { Name = "release.yml", Type = GitTreeEntryType.Blob }]);
        _cli.GetBlobAsync("/srv/git/repo.git", commit, ".pipeline/release.yml", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = ".pipeline/release.yml", Content = yaml });
        _pipelineService.GetWebhookTriggeredPipelinesForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns([new PipelineDto
            {
                Id = 12, Name = "release", ProjectId = 7,
                TriggerType = PipelineTriggerType.Webhook, YamlDefinition = yaml
            }]);
        _pipelineService.HasActiveRunAsync(12, Arg.Any<CancellationToken>()).Returns(false);

        await _sut.HandleAsync(Push("refs/heads/main", commit), TestContext.Current.CancellationToken);

        await _pipelineService.Received(1).UpsertPipelineFromYamlAsync(
            "release", yaml, 7, "webhook", Arg.Any<CancellationToken>(), "main", "main", 1);
        await _runService.Received(1).TriggerAutomatedRunAsync(
            12, "GitPush",
            Arg.Is<Dictionary<string, string>>(variables =>
                variables["WEBHOOK_REF"] == "refs/heads/main"
                && variables["AETHEUS_SOURCE_COMMIT"] == commit),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Latest-wins. The replacement is prepared and authorized BEFORE the running one is cancelled,
    /// so a refused run cannot leave the pipeline with nothing running at all.
    /// </summary>
    [Fact]
    public async Task Handle_SupersedeRunning_ReplacesTheActiveRun()
    {
        const string commit = "fedcba9876543210fedcba9876543210fedcba98";
        const string yaml = """
            name: candidate
            trigger: webhook
            branches:
              - develop
            supersede_running: true
            stages: []
            """;
        var pipeline = new PipelineDto
        {
            Id = 24,
            Name = "candidate",
            ProjectId = 7,
            TriggerType = PipelineTriggerType.Webhook,
            YamlDefinition = yaml
        };
        var preparation = new PipelineRunPreparation
        {
            PipelineId = pipeline.Id,
            YamlSnapshot = yaml,
            TargetServerIds = []
        };
        _cli.GetTreeAsync(Arg.Any<string>(), commit, ".pipeline", Arg.Any<CancellationToken>()).Returns([]);
        _pipelineService.GetWebhookTriggeredPipelinesForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineService.HasActiveRunAsync(pipeline.Id, Arg.Any<CancellationToken>()).Returns(true);
        _pipelineRepo.GetActiveRunIdsAsync(pipeline.Id, Arg.Any<CancellationToken>()).Returns([415]);
        _runService.PrepareAutomatedRunAsync(
                pipeline.Id, "GitPush", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(preparation);
        _runService.CancelRunAsync(415, Arg.Is<PipelineRunCancellation>(c => c.By == PipelineRunCancellation.SystemAuthor), Arg.Any<CancellationToken>()).Returns(true);
        _runService.TriggerPreparedAutomatedRunAsync(
                preparation, "GitPush", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunDto { Id = 416, PipelineId = pipeline.Id });

        await _sut.HandleAsync(Push("refs/heads/develop", commit, "develop"), TestContext.Current.CancellationToken);

        await _runService.Received(1).PrepareAutomatedRunAsync(
            pipeline.Id, "GitPush",
            Arg.Is<Dictionary<string, string>>(variables =>
                variables["WEBHOOK_REF"] == "refs/heads/develop"
                && variables["AETHEUS_SOURCE_COMMIT"] == commit),
            Arg.Any<CancellationToken>());
        await _runService.Received(1).CancelRunAsync(415, Arg.Is<PipelineRunCancellation>(c => c.By == PipelineRunCancellation.SystemAuthor), Arg.Any<CancellationToken>());
        // The automated entry point: a refused replacement leaves a failed run carrying the reason.
        await _runService.Received(1).TriggerPreparedAutomatedRunAsync(
            preparation, "GitPush", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().TriggerAutomatedRunAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A push already written to disk must not be reported as failed because a trigger did not fire.
    /// The swallow-and-log came with the behaviour and is the reason this handler is dispatched as an
    /// observer rather than strictly.
    /// </summary>
    [Fact]
    public async Task Handle_TriggerFailure_DoesNotPropagate()
    {
        _cli.GetTreeAsync(Arg.Any<string>(), Arg.Any<string>(), ".pipeline", Arg.Any<CancellationToken>())
            .Returns([]);
        _pipelineService.GetWebhookTriggeredPipelinesForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns<List<PipelineDto>>(_ => throw new InvalidOperationException("pipeline store unavailable"));

        var exception = await Record.ExceptionAsync(() =>
            _sut.HandleAsync(Push("refs/heads/main", "abc"), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    // The push handler skips only a fully known diff whose paths are all ignored; uncertainty must
    // trigger a run.

    private const string DocsOnlyYaml = """
        name: candidate
        trigger: webhook
        branches:
          - develop
        paths_ignore:
          - "**/*.md"
        stages: []
        """;

    private PipelineDto DocsOnlyPipeline() => new()
    {
        Id = 24,
        Name = "candidate",
        ProjectId = 7,
        TriggerType = PipelineTriggerType.Webhook,
        YamlDefinition = DocsOnlyYaml
    };

    private void StubNoDefinitionSync() =>
        _cli.GetTreeAsync(Arg.Any<string>(), Arg.Any<string>(), ".pipeline", Arg.Any<CancellationToken>())
            .Returns([]);

    [Fact]
    public async Task Handle_PathsIgnore_PushTouchingOnlyIgnoredPaths_SkipsTheRun()
    {
        const string oldCommit = "0000000000000000000000000000000000000a";
        const string newCommit = "0000000000000000000000000000000000000b";
        StubNoDefinitionSync();
        _pipelineService.GetWebhookTriggeredPipelinesForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns([DocsOnlyPipeline()]);
        _cli.GetChangedPathsAsync("/srv/git/repo.git", oldCommit, newCommit, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>?)["README.md", "docs/guide.md"]);

        await _sut.HandleAsync(
            Push("refs/heads/develop", oldCommit, newCommit, "develop"), TestContext.Current.CancellationToken);

        await _runService.DidNotReceive().TriggerAutomatedRunAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PathsIgnore_NewBranchHasNoRangeToDiff_StillTriggers()
    {
        // git announces all-zero as the previous id for a branch this push creates: there is no range
        // to diff, so the push must build rather than being silently treated as "nothing changed".
        const string allZero = "0000000000000000000000000000000000000000";
        const string newCommit = "0000000000000000000000000000000000000b";
        StubNoDefinitionSync();
        _pipelineService.GetWebhookTriggeredPipelinesForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns([DocsOnlyPipeline()]);
        _pipelineService.HasActiveRunAsync(24, Arg.Any<CancellationToken>()).Returns(false);

        await _sut.HandleAsync(
            Push("refs/heads/develop", allZero, newCommit, "develop"), TestContext.Current.CancellationToken);

        await _cli.DidNotReceive().GetChangedPathsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _runService.Received(1).TriggerAutomatedRunAsync(
            24, "GitPush", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PathsIgnore_UnreadableDiff_StillTriggers()
    {
        // A repository whose diff cannot be read is unknown, not unchanged: the run must still happen.
        const string oldCommit = "0000000000000000000000000000000000000a";
        const string newCommit = "0000000000000000000000000000000000000b";
        StubNoDefinitionSync();
        _pipelineService.GetWebhookTriggeredPipelinesForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns([DocsOnlyPipeline()]);
        _pipelineService.HasActiveRunAsync(24, Arg.Any<CancellationToken>()).Returns(false);
        _cli.GetChangedPathsAsync("/srv/git/repo.git", oldCommit, newCommit, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ => throw new InvalidOperationException("git worktree busy"));

        await _sut.HandleAsync(
            Push("refs/heads/develop", oldCommit, newCommit, "develop"), TestContext.Current.CancellationToken);

        await _runService.Received(1).TriggerAutomatedRunAsync(
            24, "GitPush", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }
}
