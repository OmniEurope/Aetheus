// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.DependencyUpdates;
using Aetheus.Shared.Components.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// R-30: a green CI and QA of an update opens one pull request (internal repository) or announces that it has to be opened on the origin
/// (mirror repository), and notifies once. Real EF rows (in memory); the Git side and the notification service are doubles, so nothing
/// here proves a real pull request or a delivered mail: it proves what is asked of them, when, and how many times.
/// </summary>
public sealed class DependencyUpdateReadyFollowUpTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private const string Branch = "aetheus/deps/Newtonsoft.Json-13.0.3";
    private const int CiRunId = 1100;
    private const int QaRunId = 1400;

    private readonly AppDbContext _db;
    private readonly FakeMergeRequests _mergeRequests = new();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly DependencyUpdateReadyFollowUp _followUp;

    public DependencyUpdateReadyFollowUpTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(Now));
        var repository = new DependencyUpdateRepository(_db);
        _followUp = new DependencyUpdateReadyFollowUp(repository, _mergeRequests, new DependencyUpdateNotices(_notifications, repository),
            new FakeTimeProvider(Now), NullLogger<DependencyUpdateReadyFollowUp>.Instance);
        _db.Projects.Add(new Project { Id = 3, Name = "Shop" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = CiRunId, PipelineId = 11, Status = PipelineStatus.Success },
            new PipelineRun { Id = QaRunId, PipelineId = 14, Status = PipelineStatus.Success });
        _db.DependencyUpdates.Add(Update(5));
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static DependencyUpdate Update(int id) => new()
    {
        Id = id,
        OrganizationId = 2,
        ProjectId = 3,
        GitInternalRepoId = 7,
        Ecosystem = "NuGet",
        PackageName = "Newtonsoft.Json",
        FromVersion = "13.0.1",
        TargetVersion = "13.0.3",
        Advisory = "GHSA-xxxx",
        Status = DependencyUpdateStatus.BranchPushed,
        Branch = Branch,
        CommitSha = "abc123",
        CiPipelineId = 11,
        CiPipelineRunId = CiRunId,
        QaPipelineId = 14,
        QaPipelineRunId = QaRunId,
        CreatedAt = Now.UtcDateTime,
        UpdatedAt = Now.UtcDateTime
    };

    private DependencyUpdate Row() => Assert.Single(_db.DependencyUpdates.AsNoTracking());

    private Task CompleteAsync(int runId, PipelineStatus status) =>
        _followUp.HandleAsync(new PipelineRunCompletedEvent(runId, status), TestContext.Current.CancellationToken);

    private async Task SetRunAsync(int runId, PipelineStatus status)
    {
        (await _db.PipelineRuns.SingleAsync(run => run.Id == runId, TestContext.Current.CancellationToken)).Status = status;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private List<(string EventType, JsonElement Payload)> Sent() =>
        [.. _notifications.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(INotificationService.SendEventAsync))
            .Select(call => ((string)call.GetArguments()[0]!, JsonSerializer.SerializeToElement(call.GetArguments()[1])))];

    [Fact]
    public async Task CiAndQaGreen_OpensOnePullRequestOnTheBranchAndNotifiesOnce()
    {
        await CompleteAsync(QaRunId, PipelineStatus.Success);

        var opened = Assert.Single(_mergeRequests.Opened);
        Assert.Equal((7, Branch), (opened.RepositoryId, opened.Branch));
        Assert.Equal("Update Newtonsoft.Json to 13.0.3 (GHSA-xxxx)", opened.Title);
        Assert.Contains("Review and merge by hand", opened.Description, StringComparison.Ordinal);
        var row = Row();
        Assert.Equal(77, row.PullRequestId);
        Assert.Equal(Now.UtcDateTime, row.ReadyHandledAt);
        var (eventType, payload) = Assert.Single(Sent());
        Assert.Equal(NotificationEventTypes.DependencyUpdateReady, eventType);
        Assert.Equal("PullRequestOpened", payload.GetProperty("Outcome").GetString());
        Assert.Equal(("Shop", "Newtonsoft.Json", "13.0.3", Branch),
            (payload.GetProperty("ProjectName").GetString(), payload.GetProperty("PackageName").GetString(),
                payload.GetProperty("TargetVersion").GetString(), payload.GetProperty("Branch").GetString()));
        Assert.Equal(3, payload.GetProperty("ProjectId").GetInt32());
    }

    [Fact]
    public async Task AMajorUpdate_SaysSoInThePullRequest()
    {
        (await _db.DependencyUpdates.SingleAsync(TestContext.Current.CancellationToken)).IsMajorUpgrade = true;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await CompleteAsync(QaRunId, PipelineStatus.Success);

        Assert.Contains("major version", Assert.Single(_mergeRequests.Opened).Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQaCompletingTwice_AndTheSweepAfterIt_OpenOnePullRequestAndNotifyOnce()
    {
        await CompleteAsync(QaRunId, PipelineStatus.Success);
        await CompleteAsync(QaRunId, PipelineStatus.Success);
        await _followUp.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Single(_mergeRequests.Opened);
        Assert.Single(Sent());
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    [InlineData(PipelineStatus.Partial)]
    [InlineData(PipelineStatus.Rejected)]
    public async Task QaNotGreen_DoesNothing(PipelineStatus status)
    {
        await SetRunAsync(QaRunId, status);

        await CompleteAsync(QaRunId, status);
        await _followUp.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_mergeRequests.Opened);
        Assert.Empty(Sent());
        Assert.Null(Row().ReadyHandledAt);
    }

    [Fact]
    public async Task CiNotGreen_DoesNothingEvenWhenTheQaIs()
    {
        await SetRunAsync(CiRunId, PipelineStatus.Failed);

        await CompleteAsync(QaRunId, PipelineStatus.Success);
        await _followUp.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_mergeRequests.Opened);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task ARunOfAnotherPipelineOrUpdate_IsIgnored()
    {
        await CompleteAsync(9999, PipelineStatus.Success);
        await CompleteAsync(CiRunId, PipelineStatus.Success);

        Assert.Empty(_mergeRequests.Opened);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task MirrorRepository_OpensNothingAndSendsNothingToTheOrigin_ButRecordsAndNotifiesThatItMustBeOpenedThere()
    {
        _mergeRequests.IsMirror = true;

        await CompleteAsync(QaRunId, PipelineStatus.Success);
        await CompleteAsync(QaRunId, PipelineStatus.Success);

        Assert.Empty(_mergeRequests.Opened);
        var row = Row();
        Assert.Null(row.PullRequestId);
        Assert.NotNull(row.ReadyHandledAt);
        Assert.Contains("has to be opened on the origin", row.Reason, StringComparison.Ordinal);
        var (eventType, payload) = Assert.Single(Sent());
        Assert.Equal(NotificationEventTypes.DependencyUpdateReady, eventType);
        Assert.Equal("PullRequestToOpenOnOrigin", payload.GetProperty("Outcome").GetString());
    }

    [Fact]
    public async Task LostQaEvent_TheSweepOfThePassFinishesTheUpdateOnceBothRunsAreGreen()
    {
        await SetRunAsync(QaRunId, PipelineStatus.Running);
        await _followUp.ResumeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_mergeRequests.Opened);

        await SetRunAsync(QaRunId, PipelineStatus.Success);
        await _followUp.ResumeAsync(TestContext.Current.CancellationToken);
        await _followUp.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Single(_mergeRequests.Opened);
        Assert.Single(Sent());
        Assert.NotNull(Row().ReadyHandledAt);
    }

    [Fact]
    public async Task OpeningFails_TheSweepKeepsGoingAndTheUpdateIsRetriedWithoutAMail()
    {
        var second = Update(6);
        second.PackageName = "lodash";
        second.TargetVersion = "4.17.21";
        second.Branch = "aetheus/deps/lodash-4.17.21";
        second.CiPipelineRunId = 1200;
        second.QaPipelineRunId = 1500;
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 1200, PipelineId = 11, Status = PipelineStatus.Success },
            new PipelineRun { Id = 1500, PipelineId = 14, Status = PipelineStatus.Success });
        _db.DependencyUpdates.Add(second);
        _mergeRequests.FailForBranch = Branch;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _followUp.ResumeAsync(TestContext.Current.CancellationToken);

        // The update that cannot be opened is left for the next pass; the other one was finished.
        Assert.Equal(1, await _db.DependencyUpdates.CountAsync(update => update.ReadyHandledAt != null, TestContext.Current.CancellationToken));
        Assert.Single(Sent());
        var failing = await _db.DependencyUpdates.AsNoTracking().SingleAsync(update => update.Id == 5, TestContext.Current.CancellationToken);
        Assert.Null(failing.ReadyHandledAt);
    }

    private sealed class FakeMergeRequests : IDependencyUpdateMergeRequests
    {
        public bool IsMirror { get; set; }

        public string? FailForBranch { get; set; }

        public List<(int RepositoryId, string Branch, string Title, string Description)> Opened { get; } = [];

        public Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct) =>
            Task.FromResult<CveRemediationRepositoryRow?>(new CveRemediationRepositoryRow(
                new GitInternalRepo { Id = repositoryId, ProjectId = projectId, Slug = "shop", DefaultBranch = "main" }, IsMirror));

        public Task<int> OpenAsync(GitInternalRepo repository, string branch, string title, string description, CancellationToken ct)
        {
            if (branch == FailForBranch) throw new InvalidOperationException("the git service is down");
            Opened.Add((repository.Id, branch, title, description));
            return Task.FromResult(77);
        }

        public Task<DependencyUpdateRetirement> RetireAsync(GitInternalRepo repository, string branch, int? pullRequestId, CancellationToken ct) =>
            throw new NotSupportedException("The ready step never retires a branch.");
    }
}
