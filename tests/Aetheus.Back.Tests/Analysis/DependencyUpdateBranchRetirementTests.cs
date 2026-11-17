// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Notifications;
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
/// R-30: the branch of an update whose vulnerability is closed is deleted on an internal repository, kept on a mirror, once, and the
/// result is notified. Real EF rows (in memory); the Git side and the notification service are doubles, so nothing here proves a
/// real deletion or a delivered mail: it proves what is asked of them, when, and how many times.
/// </summary>
public sealed class DependencyUpdateBranchRetirementTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private const string Branch = "aetheus/deps/Newtonsoft.Json-13.0.3";

    private readonly AppDbContext _db;
    private readonly FakeMergeRequests _mergeRequests = new();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly DependencyUpdateBranchRetirement _retirement;

    public DependencyUpdateBranchRetirementTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(Now));
        var repository = new DependencyUpdateRepository(_db);
        _retirement = new DependencyUpdateBranchRetirement(repository, _mergeRequests, new DependencyUpdateNotices(_notifications, repository),
            new FakeTimeProvider(Now), NullLogger<DependencyUpdateBranchRetirement>.Instance);
        _db.Projects.Add(new Project { Id = 3, Name = "Shop" });
        _db.VulnerabilityMatches.Add(new VulnerabilityMatch { Id = 9, OrganizationId = 2, ProjectId = 3, AdvisoryId = 1, ClosedAt = Now.UtcDateTime });
        _db.DependencyUpdates.Add(Update(5, 9));
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static DependencyUpdate Update(int id, long? matchId) => new()
    {
        Id = id,
        OrganizationId = 2,
        ProjectId = 3,
        GitInternalRepoId = 7,
        VulnerabilityMatchId = matchId,
        Ecosystem = "NuGet",
        PackageName = id == 5 ? "Newtonsoft.Json" : "lodash",
        FromVersion = "13.0.1",
        TargetVersion = "13.0.3",
        Advisory = "GHSA-xxxx",
        Status = DependencyUpdateStatus.BranchPushed,
        Branch = id == 5 ? Branch : "aetheus/deps/lodash-13.0.3",
        CommitSha = "abc123",
        PullRequestId = 77,
        CreatedAt = Now.UtcDateTime,
        UpdatedAt = Now.UtcDateTime
    };

    private DependencyUpdate Row() => Assert.Single(_db.DependencyUpdates.AsNoTracking());

    private Task RunAsync() => _retirement.RunAsync(TestContext.Current.CancellationToken);

    private List<(string EventType, JsonElement Payload)> Sent() =>
        [.. _notifications.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(INotificationService.SendEventAsync))
            .Select(call => ((string)call.GetArguments()[0]!, JsonSerializer.SerializeToElement(call.GetArguments()[1])))];

    [Fact]
    public async Task ClosedVulnerability_DeletesTheBranchOfTheInternalRepositoryAndNotifies()
    {
        await RunAsync();

        var retired = Assert.Single(_mergeRequests.Retired);
        Assert.Equal((7, Branch, 77), (retired.RepositoryId, retired.Branch, retired.PullRequestId));
        var row = Row();
        Assert.Equal(Now.UtcDateTime, row.BranchRetiredAt);
        Assert.Null(row.Reason);
        var (eventType, payload) = Assert.Single(Sent());
        Assert.Equal(NotificationEventTypes.DependencyUpdateClosed, eventType);
        Assert.Equal("BranchDeleted", payload.GetProperty("Outcome").GetString());
        Assert.Equal("Shop", payload.GetProperty("ProjectName").GetString());
    }

    [Fact]
    public async Task StillOpenVulnerability_KeepsTheBranch()
    {
        (await _db.VulnerabilityMatches.SingleAsync(TestContext.Current.CancellationToken)).ClosedAt = null;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync();

        Assert.Empty(_mergeRequests.Retired);
        Assert.Empty(Sent());
        Assert.Null(Row().BranchRetiredAt);
    }

    [Fact]
    public async Task AnUpdateWhoseMatchIsGone_IsNotTakenForResolved()
    {
        (await _db.DependencyUpdates.SingleAsync(TestContext.Current.CancellationToken)).VulnerabilityMatchId = null;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync();

        Assert.Empty(_mergeRequests.Retired);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task RunTwice_DeletesAndNotifiesOnce()
    {
        await RunAsync();
        await RunAsync();

        Assert.Single(_mergeRequests.Retired);
        Assert.Single(Sent());
    }

    [Fact]
    public async Task BranchAlreadyGone_IsNotAnError_AndIsSaidSo()
    {
        _mergeRequests.BranchExists = false;

        await RunAsync();

        var row = Row();
        Assert.NotNull(row.BranchRetiredAt);
        Assert.Null(row.Reason);
        Assert.Equal("BranchAlreadyGone", Assert.Single(Sent()).Payload.GetProperty("Outcome").GetString());
    }

    [Fact]
    public async Task BranchKeptByTheGitService_IsRecordedAndNotified_WithItsReason()
    {
        _mergeRequests.Refusal = "Branch 'aetheus/deps/x' is protected by rule '*' and cannot be deleted.";

        await RunAsync();

        var row = Row();
        Assert.NotNull(row.BranchRetiredAt);
        Assert.Contains("protected by rule", row.Reason, StringComparison.Ordinal);
        var (_, payload) = Assert.Single(Sent());
        Assert.Equal("BranchKeptProtected", payload.GetProperty("Outcome").GetString());
        Assert.Contains("protected by rule", payload.GetProperty("Detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MirrorRepository_NeverDeletesNorPushes_ButRecordsAPendingDecisionAndNotifies()
    {
        _mergeRequests.IsMirror = true;

        await RunAsync();
        await RunAsync();

        Assert.Empty(_mergeRequests.Retired);
        var row = Row();
        Assert.NotNull(row.BranchRetiredAt);
        Assert.Contains("needs your agreement", row.Reason, StringComparison.Ordinal);
        var (eventType, payload) = Assert.Single(Sent());
        Assert.Equal(NotificationEventTypes.DependencyUpdateClosed, eventType);
        Assert.Equal("BranchKeptMirror", payload.GetProperty("Outcome").GetString());
    }

    [Fact]
    public async Task OneBranchThatCannotBeRetired_DoesNotStopTheOthers_AndIsRetriedNextPass()
    {
        _db.VulnerabilityMatches.Add(new VulnerabilityMatch { Id = 10, OrganizationId = 2, ProjectId = 3, AdvisoryId = 2, ClosedAt = Now.UtcDateTime });
        _db.DependencyUpdates.Add(Update(6, 10));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _mergeRequests.FailForBranch = Branch;

        await RunAsync();

        var rows = await _db.DependencyUpdates.AsNoTracking().OrderBy(update => update.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Null(rows[0].BranchRetiredAt);
        Assert.NotNull(rows[1].BranchRetiredAt);
        Assert.Single(Sent());

        _mergeRequests.FailForBranch = null;
        await RunAsync();

        Assert.NotNull((await _db.DependencyUpdates.AsNoTracking().SingleAsync(update => update.Id == 5, TestContext.Current.CancellationToken)).BranchRetiredAt);
        Assert.Equal(2, Sent().Count);
    }

    private sealed class FakeMergeRequests : IDependencyUpdateMergeRequests
    {
        public bool IsMirror { get; set; }

        public bool BranchExists { get; set; } = true;

        public string? Refusal { get; set; }

        public string? FailForBranch { get; set; }

        public List<(int RepositoryId, string Branch, int? PullRequestId)> Retired { get; } = [];

        public Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct) =>
            Task.FromResult<CveRemediationRepositoryRow?>(new CveRemediationRepositoryRow(
                new GitInternalRepo { Id = repositoryId, ProjectId = projectId, Slug = "shop", DefaultBranch = "main" }, IsMirror));

        public Task<int> OpenAsync(GitInternalRepo repository, string branch, string title, string description, CancellationToken ct) =>
            throw new NotSupportedException("The retirement never opens a pull request.");

        public Task<DependencyUpdateRetirement> RetireAsync(GitInternalRepo repository, string branch, int? pullRequestId, CancellationToken ct)
        {
            if (branch == FailForBranch) throw new InvalidOperationException("the git service is down");
            Retired.Add((repository.Id, branch, pullRequestId));
            return Task.FromResult(new DependencyUpdateRetirement(BranchExists, Refusal));
        }
    }
}
