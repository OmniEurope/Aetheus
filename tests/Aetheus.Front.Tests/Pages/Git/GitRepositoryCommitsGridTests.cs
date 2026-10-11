// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// R2-003 / R2-004 / R2-005 / R2-037 on the repository page: no toolbar above the commits grid (its
/// filters live in the columns), the Branch column as a badge, the zip download in the header actions,
/// and the blue Merge (here) and Open (repositories list) row actions.
/// </summary>
public class GitRepositoryCommitsGridTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoryCommitsGridTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/git/repos/1", new GitLightRepoDto
        {
            Id = 1,
            ProjectId = 1,
            ProjectName = "MyProject",
            Name = "my-repo",
            DefaultBranch = "main",
            CreatedAt = DateTime.UtcNow
        });
        _handler.SetJsonResponse("api/git/repos/1/filter-values", new GitRepositoryDetailFilterValuesDto
        {
            CommitAuthors = ["Dev"],
            CommitBranches = ["feature/login", "main"]
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branches", new List<GitLightBranchDto> { new() { Name = "main", IsDefault = true } });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tags", new List<GitLightTagDto>());
        _handler.SetJsonResponse("api/git/repos/1/commits", new PaginatedResult<GitLightCommitDto>
        {
            Items =
            [
                new GitLightCommitDto
                {
                    Sha = "abc123456789", ShortSha = "abc1234", Message = "feat: add login", AuthorName = "Dev",
                    AuthorDate = DateTime.UtcNow, CommitDate = DateTime.UtcNow, SourceRef = "feature/login"
                }
            ],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tree", new List<GitLightTreeEntryDto>());
        _handler.SetJsonResponse("api/git/repos/1/pull-requests", new PaginatedResult<InternalPullRequestDto>
        {
            Items =
            [
                new InternalPullRequestDto
                {
                    Number = 4, Title = "Login", SourceBranch = "feature/login", TargetBranch = "main",
                    AuthorLogin = "dev", Status = PullRequestStatus.Open, CreatedAt = DateTime.UtcNow
                }
            ],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branch-protection", new List<BranchProtectionRuleDto>());
        _handler.SetJsonResponse("api/git/repos/1/graph-data", new List<GitLightCommitDto>());
    }

    private IRenderedComponent<GitRepositoryDetail> RenderPage(string? query = null)
    {
        if (query is not null)
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/git-repositories/1{query}");
        var cut = Render<GitRepositoryDetail>(parameters => parameters.Add(page => page.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("my-repo", cut.Markup), TimeSpan.FromSeconds(3));
        return cut;
    }

    [Fact]
    public void CommitsTab_HasNoToolbar_AboveTheGrid()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("feat: add login", cut.Markup), TimeSpan.FromSeconds(3));
        // The removed toolbar: a branch dropdown, a search box and a Search button.
        Assert.DoesNotContain("SearchCommits", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[aria-label='Branch']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Search");
    }

    [Fact]
    public void Commits_WalkEveryRef_AndTheBranchColumnShowsABadge()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_handler.Requests, request =>
                request.Url.Contains("api/git/repos/1/commits", StringComparison.Ordinal)
                && (request.Url.Contains("ref=%2A", StringComparison.OrdinalIgnoreCase) || request.Url.Contains("ref=*", StringComparison.Ordinal)));
            Assert.Contains(cut.FindAll(".omni-badge"), badge => badge.TextContent.Trim() == "feature/login");
        }, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void SearchDeepLink_BecomesTheMessageColumnFilter()
    {
        RenderPage("?tab=commits&search=deadbeef");

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/git/repos/1/commits", StringComparison.Ordinal)
            && request.Url.Contains("Filters%5B0%5D.Field=Message", StringComparison.Ordinal)
            && request.Url.Contains("Filters%5B0%5D.Value=deadbeef", StringComparison.Ordinal)
            && !request.Url.Contains("search=", StringComparison.Ordinal));
    }

    [Fact]
    public void Header_OffersTheZipDownload_AsASecondaryAction()
    {
        var cut = RenderPage();

        var download = cut.FindAll("button").Single(button => button.TextContent.Contains("DownloadZip", StringComparison.Ordinal));
        Assert.Contains("omni-button--secondary", download.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void Download_FetchesTheArchiveRoute()
    {
        _handler.SetRawResponse(HttpMethod.Get, "api/git/repos/1/archive", "PK", "application/zip");
        var cut = RenderPage();

        cut.FindAll("button").Single(button => button.TextContent.Contains("DownloadZip", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Method == "GET" && request.Url.Contains("api/git/repos/1/archive", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void OpenPullRequest_MergeIsBlue_CloseStaysRed()
    {
        var cut = RenderPage("?tab=pulls");

        cut.WaitForAssertion(() =>
        {
            var merge = cut.FindAll("button[title='Merge']").Single();
            Assert.Contains("omni-button--primary", merge.ClassName, StringComparison.Ordinal);
            Assert.Contains("omni-button--danger", cut.FindAll("button[title='Close']").Single().ClassName, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CommitRowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("feat: add login", cut.Markup), TimeSpan.FromSeconds(3));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }
}
