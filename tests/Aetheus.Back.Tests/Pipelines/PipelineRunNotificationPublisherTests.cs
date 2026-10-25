// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aetheus.Back.Tests;

public sealed class PipelineRunNotificationPublisherTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IUserNotificationService _userNotifications = Substitute.For<IUserNotificationService>();
    private readonly PipelineRunNotificationPublisher _sut;

    public PipelineRunNotificationPublisherTests()
    {
        _sut = new PipelineRunNotificationPublisher(_repo, _userNotifications);
        var pipeline = new Pipeline { Id = 4, Name = "build", EnvironmentId = 2 };
        _repo.GetPipelineRunWithPipelineAsync(9, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 9, PipelineId = 4, Pipeline = pipeline, BuildNumber = 12 });
        _repo.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(10);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed, NotificationEventTypes.PipelineFailed)]
    [InlineData(PipelineStatus.Success, NotificationEventTypes.PipelineSucceeded)]
    public async Task TerminalRun_RecordsItsEvent_WithTheOwningProject(PipelineStatus status, string expectedType)
    {
        await _sut.PublishRunCompletedAsync(9, status, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            expectedType,
            Arg.Is<string>(json => UserNotificationService.ReadProjectId(json) == 10),
            Arg.Any<CancellationToken>(),
            null,
            PipelineRunNotificationPublisher.EmailOnceMarker(9));
    }

    [Theory]
    [InlineData(PipelineStatus.Cancelled)]
    [InlineData(PipelineStatus.Partial)]
    [InlineData(PipelineStatus.RolledBack)]
    public async Task OtherStatuses_RecordNothing(PipelineStatus status)
    {
        await _sut.PublishRunCompletedAsync(9, status, TestContext.Current.CancellationToken);

        await _userNotifications.DidNotReceiveWithAnyArgs()
            .RecordProjectEventAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ApprovalRequested_RecordsTheApprovalEvent_WithTheOwningProject()
    {
        await _sut.PublishApprovalRequestedAsync(
            new PipelineApprovalRequestedEvent(9, "deploy", "prod"), TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineApprovalRequested,
            Arg.Is<string>(json => UserNotificationService.ReadProjectId(json) == 10),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The bridge records user deliveries only: resolved from a container where the admin notification
    /// service is available, none of the three pipeline events ever reaches SendEventAsync (admin channel
    /// rules and AI triggers).
    /// </summary>
    [Fact]
    public async Task PipelineEvents_NeverReachSendEventAsync()
    {
        var notifications = Substitute.For<INotificationService>();
        await using var provider = new ServiceCollection()
            .AddSingleton(_repo)
            .AddSingleton(_userNotifications)
            .AddSingleton(notifications)
            .AddScoped<PipelineRunNotificationPublisher>()
            .BuildServiceProvider();
        var publisher = provider.GetRequiredService<PipelineRunNotificationPublisher>();

        await publisher.PublishRunCompletedAsync(9, PipelineStatus.Failed, TestContext.Current.CancellationToken);
        await publisher.PublishRunCompletedAsync(9, PipelineStatus.Success, TestContext.Current.CancellationToken);
        await publisher.PublishApprovalRequestedAsync(
            new PipelineApprovalRequestedEvent(9, "deploy", "prod"), TestContext.Current.CancellationToken);

        await _userNotifications.ReceivedWithAnyArgs(3)
            .RecordProjectEventAsync(default!, default!, TestContext.Current.CancellationToken);
        await notifications.DidNotReceiveWithAnyArgs()
            .SendEventAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task R177_ARunStartedByAnotherPipeline_IsMailedOnlyToTheSubscribersWhoAskedForChildResults()
    {
        _repo.GetRootRunReferencesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineRunRootReference> { [9] = new(3, 1, "nightly") });

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Failed, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed,
            Arg.Is<string>(json => json.Contains("\"IsChildRun\":true") && json.Contains("\"RootRunId\":3")),
            Arg.Any<CancellationToken>(),
            NotificationEventTypes.PipelineChildResults,
            PipelineRunNotificationPublisher.EmailOnceMarker(9));
    }

    [Fact]
    public async Task R177_ARootRun_IsMailedLikeBefore_AndNeverGatedByTheChildOption()
    {
        _repo.GetRootRunReferencesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineRunRootReference> { [9] = new(9, 4, "build") });

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Success, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineSucceeded,
            Arg.Is<string>(json => json.Contains("\"IsChildRun\":false")),
            Arg.Any<CancellationToken>(),
            null,
            PipelineRunNotificationPublisher.EmailOnceMarker(9));
    }

    [Fact]
    public async Task R177_ARejectedRun_IsRecordedAsItsOwnResult()
    {
        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Rejected, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineRejected,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            null,
            PipelineRunNotificationPublisher.EmailOnceMarker(9));
    }

    [Fact]
    public void R177_TheChildResultsOption_IsOffByDefault_AndIsNotAnEventNobodyIsMailedFor()
    {
        var option = NotificationEventTypes.Find(NotificationEventTypes.PipelineChildResults);

        Assert.NotNull(option);
        Assert.False(option.DefaultEnabled);
        Assert.False(option.SentByEmail);
    }

    [Fact]
    public async Task CompletedHandler_QueuesOnlyRecordedStatuses()
    {
        var queue = Substitute.For<IBackgroundTaskQueue>();
        var handler = new PipelineRunCompletedNotificationHandler(queue);

        await handler.HandleAsync(new PipelineRunCompletedEvent(9, PipelineStatus.Failed), TestContext.Current.CancellationToken);
        await handler.HandleAsync(new PipelineRunCompletedEvent(9, PipelineStatus.Cancelled), TestContext.Current.CancellationToken);

        queue.Received(1).Enqueue(Arg.Any<Func<IServiceProvider, CancellationToken, Task>>());
    }

    [Fact]
    public async Task R177_AChildResult_NamesTheRootRun_ItsMailLinksTo()
    {
        _repo.GetRootRunReferencesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineRunRootReference> { [9] = new(3, 1, "nightly") });

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Failed, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed,
            Arg.Is<string>(json => json.Contains("\"RootRunId\":3") && json.Contains("\"RootPipelineName\":\"nightly\"")),
            Arg.Any<CancellationToken>(),
            NotificationEventTypes.PipelineChildResults,
            Arg.Any<string?>());
    }

    [Fact]
    public async Task R177_TheOnceMarker_MatchesThePayloadTheRunResultIsRecordedWith()
    {
        string? payload = null;
        string? marker = null;
        await _userNotifications.RecordProjectEventAsync(
            Arg.Any<string>(), Arg.Do<string>(json => payload = json), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Do<string?>(value => marker = value));

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Success, TestContext.Current.CancellationToken);

        Assert.NotNull(marker);
        Assert.Equal(PipelineRunNotificationPublisher.EmailOnceMarker(9), marker);
        Assert.StartsWith(marker, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R177_ARootRun_CarriesNoRootPipelineName()
    {
        _repo.GetRootRunReferencesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineRunRootReference> { [9] = new(9, 4, "build") });

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Success, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineSucceeded,
            Arg.Is<string>(json => json.Contains("\"RootRunId\":9") && json.Contains("\"RootPipelineName\":null")),
            Arg.Any<CancellationToken>(),
            Arg.Is<string?>(value => value == null),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task R19_AFailedRun_CarriesItsCauseAndTheFailedStep_InThePayload()
    {
        _repo.GetFailedStepsAsync(9, 3, Arg.Any<CancellationToken>()).Returns([
            new StepFailureProjection("Deploy", "restart", null, 2, "SSH_TIMEOUT", "The host did not answer"),
            new StepFailureProjection("Deploy", "verify", null, 1, null, null)]);
        string? payload = null;
        await _userNotifications.RecordProjectEventAsync(
            Arg.Any<string>(), Arg.Do<string>(json => payload = json), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<string?>());

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Failed, TestContext.Current.CancellationToken);

        using var document = System.Text.Json.JsonDocument.Parse(payload!);
        Assert.Equal("The host did not answer (SSH_TIMEOUT)", document.RootElement.GetProperty("Cause").GetString());
        Assert.Equal("Deploy / restart, Deploy / verify", document.RootElement.GetProperty("CauseStep").GetString());
    }

    [Fact]
    public async Task R19_ARejectedRun_CarriesTheRefusingOutputs_InThePayload()
    {
        _repo.GetSuccessfulStepOutputsAsync(9, Arg.Any<CancellationToken>()).Returns([
            new StepOutputProjection("Qa", "seal", """{"UNIT_TEST_GATE_STATUS":"3","SMOKE_GATE_STATUS":"2","CANDIDATE_DEPLOYABLE":"false"}"""),
            new StepOutputProjection("Qa", "other", """{"FOO":"bar"}""")]);
        string? payload = null;
        await _userNotifications.RecordProjectEventAsync(
            Arg.Any<string>(), Arg.Do<string>(json => payload = json), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<string?>());

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Rejected, TestContext.Current.CancellationToken);

        using var document = System.Text.Json.JsonDocument.Parse(payload!);
        Assert.Equal("UNIT_TEST_GATE_STATUS=3, CANDIDATE_DEPLOYABLE=false", document.RootElement.GetProperty("Cause").GetString());
        Assert.Equal("Qa / seal", document.RootElement.GetProperty("CauseStep").GetString());
    }

    [Fact]
    public async Task R19_ASuccessfulRun_NamesNoCause_AndNeverReadsTheStepsForIt()
    {
        string? payload = null;
        await _userNotifications.RecordProjectEventAsync(
            Arg.Any<string>(), Arg.Do<string>(json => payload = json), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<string?>());

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Success, TestContext.Current.CancellationToken);

        using var document = System.Text.Json.JsonDocument.Parse(payload!);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("Cause").ValueKind);
        await _repo.DidNotReceiveWithAnyArgs().GetFailedStepsAsync(default, default, TestContext.Current.CancellationToken);
        await _repo.DidNotReceiveWithAnyArgs().GetSuccessfulStepOutputsAsync(default, TestContext.Current.CancellationToken);
    }

    /// <summary>Confirmed R1-9: an on_success follow-up is launched without any trigger step pointing at it, so it
    /// is its own root: its result is mailed to every recipient, like a run started alone.</summary>
    [Fact]
    public async Task R19_AnOnSuccessFollowUp_IsItsOwnRoot_AndIsMailedLikeARootRun()
    {
        _repo.GetRootRunReferencesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineRunRootReference>());

        await _sut.PublishRunCompletedAsync(9, PipelineStatus.Failed, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed,
            Arg.Is<string>(json => json.Contains("\"IsChildRun\":false") && json.Contains("\"RootRunId\":9")),
            Arg.Any<CancellationToken>(),
            null,
            PipelineRunNotificationPublisher.EmailOnceMarker(9));
    }
}
