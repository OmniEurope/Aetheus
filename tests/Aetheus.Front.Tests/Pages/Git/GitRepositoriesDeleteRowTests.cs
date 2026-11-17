// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-10 on the repositories grid: a deleted repository leaves the table at once, before the list is read again
/// and without the grid's blocking bar; a refused deletion keeps the row. (A repository is created and edited on its own
/// page, not in this list, so deletion is the only row change this grid sees.)</summary>
public sealed class GitRepositoriesDeleteRowTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoriesDeleteRowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/git/repos/filter-values", new GitRepositoryFilterValuesDto());
    }

    private static GitLightRepoDto Repo(int id, string name) =>
        new() { Id = id, Name = name, ProjectId = 3, ProjectName = "Zeta", DefaultBranch = "main" };

    private static PaginatedResult<GitLightRepoDto> Page(params GitLightRepoDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    [Fact]
    public async Task ADeletedRepository_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/git/repos?", Page(Repo(10, "alpha-repo"), Repo(11, "beta-repo")));
        var cut = Render<GitRepositories>();
        cut.WaitForAssertion(() => Assert.Contains("beta-repo", cut.Markup, StringComparison.Ordinal));
        var grid = cut.FindComponent<AetheusDataGrid<GitLightRepoDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/11", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("beta-repo", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/11", HttpStatusCode.NoContent);
        var gate = new TaskCompletionSource<PaginatedResult<GitLightRepoDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos?", _ => gate.Task);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("beta-repo", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("alpha-repo", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Repo(10, "alpha-repo")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("beta-repo", cut.Markup, StringComparison.Ordinal);
    }
}
