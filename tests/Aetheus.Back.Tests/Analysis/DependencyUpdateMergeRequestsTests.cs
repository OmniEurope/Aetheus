// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// R-30: the pull request and branch side of an update, over the platform's own pull request rows (real EF, in memory) and the Git
/// services (doubles): one pull request per branch, a branch only deleted when it is an update branch, a branch already gone is fine.
/// </summary>
public sealed class DependencyUpdateMergeRequestsTests : IDisposable
{
    private const string Branch = "aetheus/deps/Newtonsoft.Json-13.0.3";
    private const string DiskPath = "/repos/shop";

    private readonly AppDbContext _db;
    private readonly IGitLightService _light = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly DependencyUpdateMergeRequests _sut;
    private readonly GitInternalRepo _repository = new() { Id = 7, ProjectId = 3, Slug = "shop", DefaultBranch = "main", GitConnectionId = 4 };

    public DependencyUpdateMergeRequestsTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)));
        _sut = new DependencyUpdateMergeRequests(new CveRemediationRepository(_db), _light, _cli);
        _light.ResolveDiskPath(3, "shop").Returns(DiskPath);
        _light.CreatePullRequestAsync(7, Arg.Any<CreateInternalPullRequestRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InternalPullRequestDto { Id = 55, Number = 12 });
        _cli.GetBranchesAsync(DiskPath, "main", Arg.Any<CancellationToken>())
            .Returns([new GitLightBranchDto { Name = "main", IsDefault = true }, new GitLightBranchDto { Name = Branch }]);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Open_CreatesThePullRequestFromTheBranchIntoTheDefaultBranch_AsTheUpdatesAuthor()
    {
        var id = await _sut.OpenAsync(_repository, Branch, "Update", "Description", Ct);

        Assert.Equal(55, id);
        await _light.Received(1).CreatePullRequestAsync(
            7,
            Arg.Is<CreateInternalPullRequestRequest>(request => request.SourceBranch == Branch && request.TargetBranch == "main"
                && request.Title == "Update" && request.Description == "Description"),
            DependencyUpdateGit.Author, Ct);
    }

    [Fact]
    public async Task Open_ReusesThePullRequestAlreadyOpenFromTheBranch()
    {
        _db.PullRequests.Add(new PullRequest { Id = 31, GitConnectionId = 4, ExternalId = 9, SourceBranch = Branch, TargetBranch = "main", Status = PullRequestStatus.Open });
        await _db.SaveChangesAsync(Ct);

        var id = await _sut.OpenAsync(_repository, Branch, "Update", "Description", Ct);

        Assert.Equal(31, id);
        await _light.DidNotReceiveWithAnyArgs().CreatePullRequestAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task Retire_ClosesTheOpenPullRequestThenDeletesTheBranch()
    {
        _db.PullRequests.Add(new PullRequest { Id = 31, GitConnectionId = 4, ExternalId = 9, SourceBranch = Branch, TargetBranch = "main", Status = PullRequestStatus.Open });
        await _db.SaveChangesAsync(Ct);

        var result = await _sut.RetireAsync(_repository, Branch, 31, Ct);

        Assert.Equal(new DependencyUpdateRetirement(true, null), result);
        await _light.Received(1).ClosePullRequestAsync(7, 9, Ct);
        await _light.Received(1).DeleteBranchAsync(7, Branch, Ct);
    }

    [Fact]
    public async Task Retire_LeavesAMergedPullRequestAlone()
    {
        _db.PullRequests.Add(new PullRequest { Id = 31, GitConnectionId = 4, ExternalId = 9, SourceBranch = Branch, TargetBranch = "main", Status = PullRequestStatus.Merged });
        await _db.SaveChangesAsync(Ct);

        await _sut.RetireAsync(_repository, Branch, 31, Ct);

        await _light.DidNotReceiveWithAnyArgs().ClosePullRequestAsync(default, default, Ct);
        await _light.Received(1).DeleteBranchAsync(7, Branch, Ct);
    }

    [Fact]
    public async Task Retire_ABranchAlreadyGone_IsNotAnErrorAndDeletesNothing()
    {
        _cli.GetBranchesAsync(DiskPath, "main", Arg.Any<CancellationToken>()).Returns([new GitLightBranchDto { Name = "main", IsDefault = true }]);

        var result = await _sut.RetireAsync(_repository, Branch, null, Ct);

        Assert.Equal(new DependencyUpdateRetirement(false, null), result);
        await _light.DidNotReceiveWithAnyArgs().DeleteBranchAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Retire_AProtectedBranch_IsKeptWithTheGitServicesReason()
    {
        _light.DeleteBranchAsync(7, Branch, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Branch is protected by rule '*' and cannot be deleted.")));

        var result = await _sut.RetireAsync(_repository, Branch, null, Ct);

        Assert.True(result.Existed);
        Assert.Contains("protected by rule", result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("aetheus/remediation/x")]
    public async Task Retire_NeverDeletesABranchThatIsNotAnUpdateBranch(string branch)
    {
        var result = await _sut.RetireAsync(_repository, branch, null, Ct);

        Assert.NotNull(result.Refusal);
        await _light.DidNotReceiveWithAnyArgs().DeleteBranchAsync(default, default!, Ct);
    }
}
