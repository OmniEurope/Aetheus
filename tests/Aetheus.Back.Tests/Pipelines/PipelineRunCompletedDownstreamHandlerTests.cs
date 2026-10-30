// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Pipelines;
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
    private readonly IUserNotificationService _userNotifications = Substitute.For<IUserNotificationService>();
    private readonly PipelineRunCompletedDownstreamHandler _sut;

    public PipelineRunCompletedDownstreamHandlerTests()
    {
        _sut = new PipelineRunCompletedDownstreamHandler(
            _repo, _runService, new PipelineRunNotificationPublisher(_repo, _userNotifications),
            NullLogger<PipelineRunCompletedDownstreamHandler>.Instance);
        _runService.TriggerChainedRunAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(PipelineChainedRunOutcome.Started(new PipelineRunDto { Id = 500 }));
    }

    private void GivenChainedLaunch(Func<PipelineChainedRunOutcome> outcome) =>
        _runService.TriggerChainedRunAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => outcome());

    private async Task RunAsync() =>
        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

    private async Task AssertRefusalShownAsync(string reason, int pipelineId)
    {
        // The notification payload is JSON: an apostrophe or quote of the reason is escaped in it.
        var jsonReason = System.Text.Json.JsonSerializer.Serialize(reason)[1..^1];
        // On the upstream run, where its page shows the warnings...
        await _repo.Received(1).AppendRunWarningsAsync(99,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Count == 1 && w.Single().Contains("'B'") && w.Single().Contains(reason)),
            Arg.Any<CancellationToken>());
        // ...and to the project's subscribers, who open the upstream run from the notification.
        await _userNotifications.Received(1).RecordProjectEventAsync(
            "pipeline.launch-refused",
            Arg.Is<string>(p => p.Contains("\"PipelineRunId\":99") && p.Contains($"\"PipelineId\":{pipelineId}")
                && p.Contains("\"PipelineName\":\"B\"") && p.Contains("\"ProjectId\":7")
                && p.Contains("on_success:A#99") && p.Contains(jsonReason)),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

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

    // ---- Recette R-48: a follow-up that cannot start is told on the upstream run and to the project ----

    [Fact]
    public async Task A_started_follow_up_writes_no_warning_and_sends_no_notification()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });

        await RunAsync();

        await _repo.DidNotReceiveWithAnyArgs().AppendRunWarningsAsync(default, default!, TestContext.Current.CancellationToken);
        await _userNotifications.DidNotReceiveWithAnyArgs().RecordProjectEventAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_target_whose_yaml_cannot_be_read_is_reported_with_the_parsers_reason_on_the_upstream_run()
    {
        // The incident of 2026-10-25: the candidate succeeded, aetheus-deploy-prod did not start, nothing said why.
        const string reason = "its definition cannot be prepared: The pipeline YAML is invalid: (Line: 25, Col: 14) While scanning a plain scalar value, found invalid mapping.";
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });
        GivenChainedLaunch(() => PipelineChainedRunOutcome.Refused(reason));

        await RunAsync();

        await AssertRefusalShownAsync(reason, pipelineId: 2);
    }

    [Fact]
    public async Task A_target_that_does_not_exist_is_reported_by_name()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: null);

        await RunAsync();

        // No target id to point at: the notification names the target as written and points at the upstream pipeline.
        await AssertRefusalShownAsync("no pipeline named 'B' exists in project 7", pipelineId: 1);
        await _runService.DidNotReceiveWithAnyArgs().TriggerChainedRunAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_refused_cycle_is_reported()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{\"UPSTREAM_CHAIN\":\"2,1\"}", targetB: new Pipeline { Id = 2, Name = "B" });

        await RunAsync();

        await AssertRefusalShownAsync("already in the upstream chain [2,1,1], cycle refused", pipelineId: 2);
    }

    [Fact]
    public async Task A_chain_past_the_depth_limit_is_reported()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{\"UPSTREAM_CHAIN\":\"3,4,5,6,7,8,9,10,11\"}", targetB: new Pipeline { Id = 2, Name = "B" });

        await RunAsync();

        await AssertRefusalShownAsync("the upstream chain depth 10 reached the limit 10", pipelineId: 2);
    }

    private static bool IsTheCycleRefusal(string stored) =>
        StoredProblem.TryRead(stored, out var content)
        && content.Inner is [{ Code: "DownstreamCycle" } cycle]
        && cycle.Parameters["pipeline"] == "2";

    [Fact]
    public async Task A_refusal_is_stored_as_a_problem_the_front_says_in_the_users_language()
    {
        // R-113: the warning is a stored problem (code and parameters), not an English sentence only; the refusal
        // it wraps is written inside it, so the run page says both in French.
        Arrange(runPipelineId: 1, additionalVarsJson: "{\"UPSTREAM_CHAIN\":\"2,1\"}", targetB: new Pipeline { Id = 2, Name = "B" });

        await RunAsync();

        await _repo.Received(1).AppendRunWarningsAsync(99,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                StoredProblem.CodeOf(warnings.Single()) == "OnSuccessFollowUpNotStarted"
                && StoredProblem.ParameterOf(warnings.Single(), "target") == "B"
                && IsTheCycleRefusal(warnings.Single())
                && StoredProblem.English(warnings.Single()).StartsWith("The on_success follow-up 'B' was not started:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_launch_that_throws_is_reported_and_does_not_fail_the_upstream_run()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });
        GivenChainedLaunch(() => throw new BadRequestException("Parameter 'version' is required."));

        var exception = await Record.ExceptionAsync(RunAsync);

        Assert.Null(exception);
        await AssertRefusalShownAsync("the launch was refused: Parameter 'version' is required.", pipelineId: 2);
    }

    [Fact]
    public async Task A_refusal_that_cannot_be_recorded_never_fails_the_upstream_run()
    {
        Arrange(runPipelineId: 1, additionalVarsJson: "{}", targetB: new Pipeline { Id = 2, Name = "B" });
        GivenChainedLaunch(() => PipelineChainedRunOutcome.Refused("x"));
        _repo.AppendRunWarningsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("store unavailable"));

        var exception = await Record.ExceptionAsync(RunAsync);

        Assert.Null(exception);
    }

    [Fact]
    public async Task An_upstream_snapshot_that_cannot_be_parsed_is_reported_instead_of_skipping_its_follow_ups_silently()
    {
        var run = new PipelineRun
        {
            Id = 99,
            PipelineId = 1,
            YamlSnapshot = "name: A\ndescription: Diagnostic : liste\n",
            Pipeline = new Pipeline { Id = 1, Name = "A" }
        };
        _repo.GetPipelineRunWithPipelineAsync(99, Arg.Any<CancellationToken>()).Returns(run);

        await RunAsync();

        await _repo.Received(1).AppendRunWarningsAsync(99,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Single().Contains("on_success list is unknown")
                && w.Single().Contains("found invalid mapping")),
            Arg.Any<CancellationToken>());
        await _runService.DidNotReceiveWithAnyArgs().TriggerChainedRunAsync(default, default!, TestContext.Current.CancellationToken);
    }
}
