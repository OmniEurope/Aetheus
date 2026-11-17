// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Git;

/// <summary>R-10 on the repository's grids: a deleted branch, tag or protection rule leaves the table before the list is
/// read again and without the grid's blocking bar; a refused deletion keeps the row.</summary>
public sealed class GitRepositoryDetailRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public GitRepositoryDetailRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialogs.ConfirmResult = true;
        _handler.SetJsonResponse("api/git/repos/1", new GitLightRepoDto
        {
            Id = 1,
            ProjectId = 1,
            ProjectName = "MyProject",
            Name = "my-repo",
            DefaultBranch = "main",
            CloneUrl = "http://localhost/git/my-repo.git",
            CreatedAt = DateTime.UtcNow
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branches", Branches("main", "feature-gone"));
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tags", new List<GitLightTagDto>
        {
            new() { Name = "v1.0.0", Sha = "def5678" }, new() { Name = "v-gone", Sha = "def5679" }
        });
        _handler.SetJsonResponse("api/git/repos/1/commits", new PaginatedResult<GitLightCommitDto> { Items = [], TotalCount = 0 });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tree", new List<GitLightTreeEntryDto>());
        _handler.SetJsonResponse("api/git/repos/1/pull-requests", new PaginatedResult<InternalPullRequestDto> { Items = [], TotalCount = 0 });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branch-protection", new List<BranchProtectionRuleDto>
        {
            new() { Id = 5, Pattern = "release-gone" }, new() { Id = 6, Pattern = "release-kept" }
        });
        _handler.SetJsonResponse("api/git/repos/1/graph", "");
        _handler.SetJsonResponse("api/git/repos/1/graph-data", new List<GitLightCommitDto>());
    }

    private static List<GitLightBranchDto> Branches(params string[] names) =>
        [.. names.Select(name => new GitLightBranchDto { Name = name, IsDefault = name == "main" })];

    private IRenderedComponent<GitRepositoryDetail> Render(string tab, string expected)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/git-repositories/1?tab={tab}");
        var cut = Render<GitRepositoryDetail>(parameters => parameters.Add(page => page.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private async Task DeleteRowAsync(IRenderedComponent<GitRepositoryDetail> cut, string rowText)
    {
        var row = cut.FindAll("tbody tr").First(candidate => candidate.TextContent.Contains(rowText, StringComparison.Ordinal));
        await row.QuerySelector("button[title='Delete']")!.ClickAsync(new());
    }

    [Fact]
    public async Task ADeletedBranch_LeavesAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = Render("branches", "feature-gone");
        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/1/branches", HttpStatusCode.NoContent);
        var gate = new TaskCompletionSource<PaginatedResult<GitLightBranchDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos/1/branches", _ => gate.Task);

        await DeleteRowAsync(cut, "feature-gone");

        cut.WaitForAssertion(() => Assert.DoesNotContain("feature-gone", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("main", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));
        gate.SetResult(new PaginatedResult<GitLightBranchDto> { Items = Branches("main"), TotalCount = 1 });
    }

    [Fact]
    public async Task ADeletedTag_LeavesAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = Render("tags", "v-gone");
        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/1/tags", HttpStatusCode.NoContent);
        var gate = new TaskCompletionSource<PaginatedResult<GitLightTagDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos/1/tags", _ => gate.Task);

        await DeleteRowAsync(cut, "v-gone");

        cut.WaitForAssertion(() => Assert.DoesNotContain("v-gone", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("v1.0.0", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));
        gate.SetResult(new PaginatedResult<GitLightTagDto> { Items = [new GitLightTagDto { Name = "v1.0.0" }], TotalCount = 1 });
    }

    [Fact]
    public async Task ADeletedProtectionRule_LeavesAtOnce_AndARefusedDeletionKeepsIt()
    {
        var cut = Render("protection", "release-gone");
        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/1/branch-protection/5", HttpStatusCode.Conflict);

        await DeleteRowAsync(cut, "release-gone");

        Assert.Contains("release-gone", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/1/branch-protection/5", HttpStatusCode.NoContent);
        var gate = new TaskCompletionSource<PaginatedResult<BranchProtectionRuleDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos/1/branch-protection", _ => gate.Task);

        await DeleteRowAsync(cut, "release-gone");

        cut.WaitForAssertion(() => Assert.DoesNotContain("release-gone", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("release-kept", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));
        gate.SetResult(new PaginatedResult<BranchProtectionRuleDto> { Items = [new BranchProtectionRuleDto { Id = 6, Pattern = "release-kept" }], TotalCount = 1 });
    }
}
