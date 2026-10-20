// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class GitRepositoriesTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoriesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_GitRepositoriesPage()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "MyProject" }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());

        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // Render Create and the project filter with an empty grid state when no repositories exist.
        Assert.Contains("GitRepositories", cut.Markup);
        Assert.Contains(">Create<", cut.Markup);
        Assert.Contains("SelectProject", cut.Markup);
        Assert.Contains("NoRepositoriesFound", cut.Markup);
    }

    [Fact]
    public void ListsAllRepos_WhenNoProjectSelected()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [],
            TotalCount = 0
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 10, Name = "cross-project-repo", ProjectId = 3, ProjectName = "Zeta", DefaultBranch = "main" }
        });

        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // No project selected: the page now fetches the cross-project list (GET api/git/repos, no
        // projectId) and shows every accessible repo.
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/git/repos") && !r.Url.Contains("projectId"));
        var repos = (List<GitLightRepoDto>)typeof(GitRepositories)
            .GetField("_repos", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Single(repos);
    }

    [Fact]
    public async Task LoadData_WithProjectFilter_FetchesRepos()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Proj1" }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 10, Name = "my-repo", Slug = "my-repo", ProjectId = 1, DefaultBranch = "main" }
        });

        var cut = Render<GitRepositories>();
        cut.WaitForState(() => cut.Markup.Contains("GitRepositories"));

        typeof(GitRepositories).GetField("_projectFilter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, 1);

        var method = typeof(GitRepositories).GetMethod("LoadData", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var repos = (List<GitLightRepoDto>)typeof(GitRepositories)
            .GetField("_repos", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Single(repos);
    }

    [Fact]
    public async Task LoadData_WithoutProjectFilter_LoadsAllRepos()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Proj1" }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 5, Name = "any-repo", ProjectId = 1, ProjectName = "Proj1", DefaultBranch = "main" }
        });

        var cut = Render<GitRepositories>();

        typeof(GitRepositories).GetField("_projectFilter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, null);

        var method = typeof(GitRepositories).GetMethod("LoadData", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // No filter now loads the cross-project list instead of clearing it.
        var repos = (List<GitLightRepoDto>)typeof(GitRepositories)
            .GetField("_repos", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Single(repos);
    }

    [Fact]
    public async Task ClearFilters_ResetsProjectFilter()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Proj1" }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());

        var cut = Render<GitRepositories>();

        typeof(GitRepositories).GetField("_projectFilter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, 1);

        var method = typeof(GitRepositories).GetMethod("ClearFilters", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var filter = typeof(GitRepositories).GetField("_projectFilter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance);
        Assert.Null(filter);
    }

    [Fact]
    public async Task ShowCreateDialog_WithoutProject_LeavesTheStateUnchanged()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [],
            TotalCount = 0
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());

        var cut = Render<GitRepositories>();

        typeof(GitRepositories).GetField("_projectFilter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, null);

        // Count the repo loads issued so far (the init-time cross-project list). Creating a repo needs a
        // target project, so with no filter ShowCreateDialog must RETURN before opening the dialog - which
        // means it never reaches the post-dialog reload, so the api/git/repos GET count stays unchanged.
        // (If it reached the real OmniDialogService.OpenAsync, the awaited Task would never resolve and this
        // test would hang - so completing AND not reloading is the meaningful signal it returned early.)
        var reposLoadsBefore = _handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("api/git/repos"));

        var method = typeof(GitRepositories).GetMethod("ShowCreateDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var reposLoadsAfter = _handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("api/git/repos"));
        Assert.Equal(reposLoadsBefore, reposLoadsAfter);
    }

    /// <summary>Open is the primary blue row action; Delete remains red.</summary>
    [Fact]
    public void RepositoryRow_OpenIsBlue_DeleteStaysRed()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 10, Name = "repo", ProjectId = 3, ProjectName = "Zeta", DefaultBranch = "main" }
        });

        var cut = Render<GitRepositories>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("omni-button--primary", cut.FindAll("button[title='View']").Single().ClassName, StringComparison.Ordinal);
            Assert.Contains("omni-button--danger", cut.FindAll("button[title='Delete']").Single().ClassName, StringComparison.Ordinal);
        });
    }
}
