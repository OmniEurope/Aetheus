// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Validate commit filters before constructing Git arguments. Archive requests require
/// Read, reject option-like refs and return 404 for unknown repositories or refs.</summary>
public class GitCommitListQueryAndArchiveRouteTests
{
    private static GridFilter Filter(string field, GridFilterOperator op, string? value,
        GridFilterOperator? second = null, string? secondValue = null, GridFilterLogic logic = GridFilterLogic.And) =>
        new() { Field = field, Operator = op, Value = value, SecondOperator = second, SecondValue = secondValue, Logic = logic };

    private static string List(params string[] values) => string.Join(GridFilter.ListSeparator, values);

    [Fact]
    public void NoFilter_IsNull()
    {
        Assert.Null(GitCommitListQuery.Parse(null));
        Assert.Null(GitCommitListQuery.Parse([]));
    }

    [Fact]
    public void EachColumn_MapsToItsGitArgument()
    {
        var filter = GitCommitListQuery.Parse(
        [
            Filter("Message", GridFilterOperator.Contains, "login"),
            Filter("AuthorName", GridFilterOperator.In, List("Alice", "Bob")),
            Filter("SourceRef", GridFilterOperator.In, List("main", "feature/login")),
            Filter("CommitDate", GridFilterOperator.GreaterThanOrEqual, "2026-02-01T00:00:00Z",
                GridFilterOperator.LessThan, "2026-03-01T00:00:00Z")
        ]);

        Assert.NotNull(filter);
        Assert.Equal("login", filter.Message);
        Assert.Equal(["Alice", "Bob"], filter.Authors);
        Assert.Equal(["main", "feature/login"], filter.Branches);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), filter.Since);
        // git's --until is inclusive to the second: a strict upper bound moves one second back.
        Assert.Equal(new DateTime(2026, 2, 28, 23, 59, 59, DateTimeKind.Utc), filter.Until);
    }

    [Fact]
    public void DateRange_OneBoundAlone_IsAccepted()
    {
        var since = GitCommitListQuery.Parse([Filter("CommitDate", GridFilterOperator.GreaterThan, "2026-02-01T00:00:00Z")]);
        var until = GitCommitListQuery.Parse([Filter("CommitDate", GridFilterOperator.LessThanOrEqual, "2026-02-01T00:00:00Z")]);

        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 1, DateTimeKind.Utc), since!.Since);
        Assert.Null(since.Until);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), until!.Until);
    }

    [Theory]
    [InlineData("ShortSha")]
    [InlineData("Sha")]
    [InlineData("AuthorEmail")]
    [InlineData("AuthorDate")]
    public void UnknownColumn_IsRefused(string field)
        => Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([Filter(field, GridFilterOperator.Contains, "x")]));

    [Theory]
    [InlineData(GridFilterOperator.Equals)]
    [InlineData(GridFilterOperator.StartsWith)]
    [InlineData(GridFilterOperator.DoesNotContain)]
    [InlineData(GridFilterOperator.In)]
    public void Message_OnlyAcceptsContains(GridFilterOperator op)
        => Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([Filter("Message", op, "x")]));

    [Fact]
    public void Message_WithASecondCondition_IsRefused()
        => Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse(
            [Filter("Message", GridFilterOperator.Contains, "a", GridFilterOperator.Contains, "b")]));

    [Theory]
    [InlineData("--output=x")]
    [InlineData("main..feature")]
    [InlineData("main@{1}")]
    [InlineData("HEAD~1")]
    [InlineData("a b")]
    [InlineData("main:README.md")]
    public void Branch_ThatGitWouldReadAsAnOptionOrAnExpression_IsRefused(string branch)
        => Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse(
            [Filter("SourceRef", GridFilterOperator.In, List("main", branch))]));

    [Theory]
    [InlineData(GridFilterOperator.Contains)]
    [InlineData(GridFilterOperator.NotIn)]
    public void Branch_OnlyAcceptsAList(GridFilterOperator op)
        => Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([Filter("SourceRef", op, "main")]));

    [Fact]
    public void TwoFiltersOnOneColumn_SharingNoValue_AreRefused_NotReadAsNoFilter()
    {
        Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([
            Filter("SourceRef", GridFilterOperator.In, List("main")),
            Filter("SourceRef", GridFilterOperator.In, List("develop"))]));
        Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([
            Filter("AuthorName", GridFilterOperator.In, List("Alice")),
            Filter("AuthorName", GridFilterOperator.In, List("Bob"))]));
    }

    [Fact]
    public void Date_OrLogic_OrEqualsOrGarbage_IsRefused()
    {
        Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([Filter("CommitDate", GridFilterOperator.GreaterThanOrEqual,
            "2026-02-01T00:00:00Z", GridFilterOperator.LessThan, "2026-03-01T00:00:00Z", GridFilterLogic.Or)]));
        Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([Filter("CommitDate", GridFilterOperator.Equals, "2026-02-01T00:00:00Z")]));
        Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([Filter("CommitDate", GridFilterOperator.GreaterThanOrEqual, "yesterday")]));
    }

    // Archive route.

    private readonly IGitLightService _service = Substitute.For<IGitLightService>();
    private readonly IGitLightRepository _repos = Substitute.For<IGitLightRepository>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();

    private GitArchiveService Archives()
    {
        _repos.FindByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new GitInternalRepo
        {
            Id = 1,
            ProjectId = 7,
            Name = "My Repo",
            Slug = "my-repo",
            DefaultBranch = "develop"
        });
        _service.ResolveDiskPath(7, "my-repo").Returns("/repos/7/my-repo.git");
        return new GitArchiveService(_repos, _service, _cli);
    }

    private GitLightController Controller(bool canRead = true)
    {
        _service.GetProjectIdForRepoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(7);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(canRead);
        return new GitLightController(_service, _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader")], "test"))
                }
            }
        };
    }

    [Fact]
    public async Task Archive_WithoutRef_StreamsTheDefaultBranchAsAZip()
    {
        var ct = TestContext.Current.CancellationToken;
        var archives = Archives();
        _cli.GetArchiveStreamAsync("/repos/7/my-repo.git", "develop", "my-repo", Arg.Any<CancellationToken>())
            .Returns(new MemoryStream([0x50, 0x4B, 0x03, 0x04]));

        var result = await Controller().GetArchive(1, null, archives, ct);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/zip", file.ContentType);
        Assert.Equal("my-repo-develop.zip", file.FileDownloadName);
    }

    [Fact]
    public async Task Archive_OfABranchWithASlash_GetsASafeFileName()
    {
        var archives = Archives();
        _cli.GetArchiveStreamAsync(Arg.Any<string>(), "feature/login", "my-repo", Arg.Any<CancellationToken>())
            .Returns(new MemoryStream());

        var archive = await archives.GetArchiveAsync(1, "feature/login", TestContext.Current.CancellationToken);

        Assert.Equal("my-repo-feature-login.zip", archive!.FileName);
    }

    [Fact]
    public async Task Archive_RefReadAsAnOption_IsABadRequest_AndNeverReachesGit()
    {
        var archives = Archives();

        await Assert.ThrowsAsync<BadRequestException>(() =>
            Controller().GetArchive(1, "--output=/tmp/x", archives, TestContext.Current.CancellationToken));
        await _cli.DidNotReceiveWithAnyArgs().GetArchiveStreamAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Archive_UnknownRef_IsNotFound()
    {
        var archives = Archives();
        _cli.GetArchiveStreamAsync(Arg.Any<string>(), "gone", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Stream?)null);

        Assert.IsType<NotFoundResult>(await Controller().GetArchive(1, "gone", archives, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Archive_WithoutReadAccess_IsForbidden_AndNeverReachesGit()
    {
        var archives = Archives();

        Assert.IsType<ForbidResult>(await Controller(canRead: false).GetArchive(1, null, archives, TestContext.Current.CancellationToken));
        await _cli.DidNotReceiveWithAnyArgs().GetArchiveStreamAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }
}
