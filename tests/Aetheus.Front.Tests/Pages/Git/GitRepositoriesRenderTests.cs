// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Git;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Git;

public class GitRepositoriesRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type PageType = typeof(GitRepositories);

    public GitRepositoriesRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "MyProject", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
    }

    [Fact]
    public void Renders_ToolbarAndFilter_WithoutError()
    {
        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // Render Create and the project filter, loading filter choices during initialization.
        Assert.Contains("GitRepositories", cut.Markup);
        Assert.Contains(">Create<", cut.Markup);
        Assert.Contains("SelectProject", cut.Markup);
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/projects"));
    }

    [Fact]
    public void ProjectScopedPage_ContainsExternalRepositoryManagement_InItsTab()
    {
        _handler.SetResponse("api/external-repos/project/1", System.Net.HttpStatusCode.NotFound);
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("projectId", 1));

        var cut = Render<GitRepositories>();

        // The internal repositories first; the external repository is the second tab.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".git-repository-tabs [role='tab']").Count));
        Assert.DoesNotContain("id=\"external-repository\"", cut.Markup, StringComparison.Ordinal);
        cut.FindAll(".git-repository-tabs [role='tab']")[1].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("id=\"external-repository\"", cut.Markup);
            Assert.Single(cut.FindComponents<
                Aetheus.Front.Components.Projects.ProjectDetailSections.ProjectExternalRepoSection>());
            Assert.Contains(_handler.Requests, request =>
                request.Url.Contains("api/external-repos/project/1", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ProjectScopedPage_WithOneRepository_NavigatesDirectlyToRepository()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 31, ProjectId = 1, Name = "only-repo", DefaultBranch = "main" }
        });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("projectId", 1));

        Render<GitRepositories>();

        Assert.EndsWith("/git-repositories/31?projectId=1", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalRepositoryTab_WithOneInternalRepository_KeepsManagementSection()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 31, ProjectId = 1, Name = "only-repo", DefaultBranch = "main" }
        });
        _handler.SetResponse("api/external-repos/project/1", System.Net.HttpStatusCode.NotFound);
        var navigation = Services.GetRequiredService<NavigationManager>();
        // The external repository is the project's second tab.
        navigation.NavigateTo("git-repositories?projectId=1&tab=external");

        var cut = Render<GitRepositories>();

        cut.WaitForAssertion(() => Assert.Contains("id=\"external-repository\"", cut.Markup));
        Assert.EndsWith("/git-repositories?projectId=1&tab=external", navigation.Uri, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll(".git-repository-tabs [role='tab']").Count);
    }

    [Fact]
    public void ProjectScopedPage_WithSeveralRepositories_KeepsTheList()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 31, ProjectId = 1, Name = "first", DefaultBranch = "main" },
            new() { Id = 32, ProjectId = 1, Name = "second", DefaultBranch = "main" }
        });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("projectId", 1));

        var cut = Render<GitRepositories>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.Instance.RepositoryCount));
        Assert.Contains("projectId=1", navigation.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("/git-repositories/31", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyRepositoryLoad_KeepsGridMountedWhileLoading()
    {
        var response = new TaskCompletionSource<PaginatedResult<GitLightRepoDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(
            HttpMethod.Get,
            "api/git/repos",
            _ => response.Task);
        // The default-branch values of the column filter answer at once.
        _handler.SetAsyncJsonResponse(
            HttpMethod.Get,
            "api/git/repos/filter-values",
            _ => Task.FromResult(new GitRepositoryFilterValuesDto()));

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => _handler.Requests.Any(request => request.Method == "GET"
                && request.Url.Contains("api/git/repos", StringComparison.Ordinal)));

        Assert.Single(cut.FindComponents<Aetheus.Front.Components.Shared.AetheusDataGrid<GitLightRepoDto>>());

        // Complete the pending HTTP operation on bUnit's renderer so the component continuation
        // cannot be starved by an unrelated full-suite worker repeatedly scheduling renders.
        await cut.InvokeAsync(() => response.SetResult(new PaginatedResult<GitLightRepoDto>()));
        cut.WaitForState(
            () => !cut.FindComponent<Aetheus.Front.Components.Shared.AetheusDataGrid<GitLightRepoDto>>()
                .Instance.IsLoading);
        Assert.Contains("NoRepositoriesFound", cut.Markup);

        var repositoryRequests = _handler.Requests.Count(request =>
            request.Method == "GET"
            && request.Url.Contains("api/git/repos", StringComparison.Ordinal)
            && !request.Url.Contains("filter-values", StringComparison.Ordinal));
        // The page's own first load, the grid's, and the reload the page asks of the grid so a
        // virtualized grid shows that first result : bounded, never a loop.
        Assert.InRange(repositoryRequests, 1, 3);
    }

    [Fact]
    public async Task OnInitializedAsync_LoadsProjects()
    {
        var cut = Render<GitRepositories>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        var projects = (List<ProjectDto>)PageType.GetField("_projects", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(projects);
    }

    [Fact]
    public async Task LoadData_WithNoProjectFilter_ClearsRepos()
    {
        var cut = Render<GitRepositories>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        var method = PageType.GetMethod("LoadData", Priv)!;
        PageType.GetField("_projectFilter", Priv)!.SetValue(cut.Instance, null);
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        var repos = (List<GitLightRepoDto>)PageType.GetField("_repos", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(repos);
    }

    [Fact]
    public async Task LoadData_WithProjectFilter_CallsApi()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 1, Name = "my-repo", Slug = "my-repo", ProjectId = 1 }
        });
        var cut = Render<GitRepositories>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        var method = PageType.GetMethod("LoadData", Priv)!;
        PageType.GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        var repos = (List<GitLightRepoDto>)PageType.GetField("_repos", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(repos);
        Assert.Equal("my-repo", repos[0].Name);
    }

    [Fact]
    public async Task OnLoadDataAsync_CrossProjectSecondPage_ReplacesRowsAndKeepsServerTotal()
    {
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/git/repos?page=2&pageSize=25",
            new PaginatedResult<GitLightRepoDto>
            {
                Items =
                [
                    new()
                    {
                        Id = 26,
                        Name = "project-b-repo",
                        Slug = "project-b-repo",
                        ProjectId = 2,
                        ProjectName = "Project B"
                    }
                ],
                TotalCount = 51,
                Page = 2,
                PageSize = 25
            });
        var cut = Render<GitRepositories>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        _handler.Requests.Clear();
        await cut.InvokeAsync(() => cut.Instance.OnLoadDataAsync(
            new GridLoadArgs { Skip = 25, Top = 25, OrderBy = "Name" }));

        var request = Assert.Single(_handler.Requests, item =>
            item.Method == "GET"
            && item.Url.Contains("api/git/repos", StringComparison.Ordinal));
        Assert.Contains("page=2", request.Url);
        Assert.Contains("pageSize=25", request.Url);
        Assert.DoesNotContain("projectId=", request.Url);
        Assert.Single(cut.Instance.Repositories);
        Assert.Equal(2, cut.Instance.Repositories[0].ProjectId);
        Assert.Equal(51, cut.Instance.RepositoryCount);
    }

    [Fact]
    public async Task ClearFilters_ResetsProjectFilter()
    {
        var cut = Render<GitRepositories>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        PageType.GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);
        var method = PageType.GetMethod("ClearFilters", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        var filter = (int?)PageType.GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(filter);
    }

    [Fact]
    public async Task OnProjectFilterChanged_ReloadsData()
    {
        var cut = Render<GitRepositories>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("git-repositories?projectId=1");
        PageType.GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);
        _handler.Requests.Clear();
        var method = PageType.GetMethod("OnProjectFilterChanged", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new object()])!);
        // With a project filter set, the change handler reloads via GET api/git/repos?projectId=1.
        var requestUrl = _handler.Requests.Last(r => r.Method == "GET" && r.Url.Contains("api/git/repos")).Url;
        Assert.Contains("projectId=1", requestUrl);
    }

    [Fact]
    public void ApplyProjectFilterFromQuery_WithNullProjectId_ClearsFilter()
    {
        var cut = Render<GitRepositories>();
        // ProjectId parameter not set => null
        var method = PageType.GetMethod("ApplyProjectFilterFromQuery", Priv)!;
        method.Invoke(cut.Instance, []);
        var filter = (int?)PageType.GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(filter);
    }

    [Fact]
    public void ApplyProjectFilterFromQuery_WithUnknownProjectId_ClearsFilter()
    {
        var cut = Render<GitRepositories>();
        // Set a ProjectId that isn't in the loaded projects list
        var prop = PageType.GetProperty("ProjectId")!;
        prop.SetValue(cut.Instance, 999);
        var method = PageType.GetMethod("ApplyProjectFilterFromQuery", Priv)!;
        method.Invoke(cut.Instance, []);
        var filter = (int?)PageType.GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(filter);
    }

    [Fact]
    public void RefreshCanWrite_MirrorsPermissionServiceState()
    {
        var cut = Render<GitRepositories>();
        var permissions = Services.GetRequiredService<PermissionService>();
        var method = PageType.GetMethod("RefreshCanWrite", Priv)!;

        // With Write on Project, RefreshCanWrite yields true.
        method.Invoke(cut.Instance, []);
        Assert.True((bool)PageType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);

        // Revoke all permissions: RefreshCanWrite must now yield false (it reads the live service).
        permissions.SetPermissions([], isAdmin: false);
        method.Invoke(cut.Instance, []);
        Assert.False((bool)PageType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task OnPermissionsChanged_UpdatesCanWriteFromEvent()
    {
        var cut = Render<GitRepositories>();
        var permissions = Services.GetRequiredService<PermissionService>();

        // The subscribed handler starts true (test user holds Write on Project)...
        Assert.True((bool)PageType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);

        // ...and reacts to a real permission event: revoking Write drives _canWrite to false.
        await cut.InvokeAsync(() => permissions.SetPermissions([], isAdmin: false));
        Assert.False((bool)PageType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromPermissionChanges()
    {
        var cut = Render<GitRepositories>();
        var permissions = Services.GetRequiredService<PermissionService>();

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());

        // Pin _canWrite false, then raise a permission event granting Write. A still-subscribed
        // handler would flip it true; after dispose it must stay false (handler detached).
        PageType.GetField("_canWrite", Priv)!.SetValue(cut.Instance, false);
        var writeAll = Enum.GetValues<ResourceType>()
            .Select(rt => new EffectivePermissionDto { ResourceType = rt, ResourceId = null, Permission = Permission.Write })
            .ToList();
        permissions.SetPermissions(writeAll, isAdmin: false);
        Assert.False((bool)PageType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 1, Name = "repo-1", ProjectId = 1, ProjectName = "MyProject", CloneUrl = "https://example.org/repo-1.git", DefaultBranch = "main" }
        });

        var cut = Render<GitRepositories>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The repository and project links, then the row buttons.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }
}
