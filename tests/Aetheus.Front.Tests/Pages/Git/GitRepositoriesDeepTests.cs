// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Git;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Deep coverage for GitRepositories.razor.cs - filter by project, OnLoadData,
/// LoadData with no filter, ApplyProjectFilterFromQuery, SyncUrlWithCurrentFilter,
/// OnProjectFilterChanged, OnPermissionsChanged, DisposeAsync.
/// Dialog.OpenAsync (ShowCreateDialog) and Dialog.Confirm (DeleteRepo) excluded.
/// </summary>
public class GitRepositoriesDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoriesDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto { Id = 1, Name = "BackEnd" },
                new ProjectDto { Id = 2, Name = "FrontEnd" }
            ],
            TotalCount = 2
        });
        // The page now fetches api/git/repos on init even with no project filter (cross-project
        // "list all" view). Default it to empty so a render that doesn't call SetupRepos still succeeds;
        // SetupRepos overrides this same key when a test needs populated repos.
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
    }

    private void SetupRepos(int projectId, int count = 3)
    {
        var repos = Enumerable.Range(1, count).Select(i => new GitLightRepoDto
        {
            Id = i,
            Name = $"repo-{i}",
            CloneUrl = $"https://github.com/org/repo-{i}.git",
            DefaultBranch = "main"
        }).ToList();
        _handler.SetPaginatedJsonResponse("api/git/repos", repos);
    }

    // ── Test 1: LoadData with no filter lists every accessible repo ──────────

    [Fact]
    public async Task LoadData_WithNoFilter_LoadsAllAccessibleRepos()
    {
        SetupProjects();
        SetupRepos(1); // overrides the empty default: api/git/repos (no projectId) returns repos

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        // No filter now means "list all repos the user can read", not an empty grid.
        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, (int?)null);
        var loadMethod = typeof(GitRepositories).GetMethod("LoadData", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        var repos = (List<GitLightRepoDto>)typeof(GitRepositories).GetField("_repos", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(repos);
    }

    // ── Test 3: ApplyProjectFilterFromQuery with valid project ────────────────

    [Fact]
    public async Task ApplyProjectFilterFromQuery_ValidProject_SetsFilter()
    {
        SetupProjects();
        SetupRepos(1);

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        // Set ProjectId via reflection (bypassing [SupplyParameterFromQuery])
        typeof(GitRepositories).GetProperty("ProjectId")!.SetValue(cut.Instance, (int?)1);
        var method = typeof(GitRepositories).GetMethod("ApplyProjectFilterFromQuery", Priv)!;
        method.Invoke(cut.Instance, []);

        var filter = (int?)typeof(GitRepositories).GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Equal(1, filter);
    }

    // ── Test 4: ApplyProjectFilterFromQuery - unknown project clears filter ───

    [Fact]
    public void ApplyProjectFilterFromQuery_UnknownProject_ClearsFilter()
    {
        SetupProjects();

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        // Unknown projectId not in _projects
        typeof(GitRepositories).GetProperty("ProjectId")!.SetValue(cut.Instance, (int?)999);
        var method = typeof(GitRepositories).GetMethod("ApplyProjectFilterFromQuery", Priv)!;
        method.Invoke(cut.Instance, []);

        var filter = (int?)typeof(GitRepositories).GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(filter);
    }

    // ── Test 5: ClearFilters resets projectFilter ─────────────────────────────

    [Fact]
    public async Task ClearFilters_ResetsProjectFilter()
    {
        SetupProjects();
        SetupRepos(1);

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        // Set a filter first via reflection
        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);

        var method = typeof(GitRepositories).GetMethod("ClearFilters", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var filter = (int?)typeof(GitRepositories).GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(filter);
    }

    // ── Test 6: OnProjectFilterChanged with null filter lists all repos ──────

    [Fact]
    public async Task OnProjectFilterChanged_NullFilter_LoadsAllRepos()
    {
        SetupProjects();
        SetupRepos(1); // clearing the filter now reloads the cross-project list, not an empty grid

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, (int?)null);

        var method = typeof(GitRepositories).GetMethod("OnProjectFilterChanged", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [new object()])!);

        var repos = (List<GitLightRepoDto>)typeof(GitRepositories).GetField("_repos", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(repos);
    }

    // ── Test 7: Projects are loaded on init ───────────────────────────────────

    [Fact]
    public void OnInit_ProjectsLoaded()
    {
        SetupProjects();

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        var projects = (List<ProjectDto>)typeof(GitRepositories).GetField("_projects", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, projects.Count);
    }

    private static bool GetCanWrite(GitRepositories instance) =>
        (bool)typeof(GitRepositories).GetField("_canWrite", Priv)!.GetValue(instance)!;

    // ── Test 8: OnPermissionsChanged recomputes _canWrite from the real event ─

    [Fact]
    public async Task OnPermissionsChanged_RecomputesCanWriteFromEvent()
    {
        SetupProjects();
        var cut = Render<GitRepositories>();
        var permissions = Services.GetRequiredService<PermissionService>();

        // Test user starts with Write on Project - the subscribed handler has _canWrite true.
        Assert.True(GetCanWrite(cut.Instance));

        // Revoking Write through the real event must drive _canWrite false...
        await cut.InvokeAsync(() => permissions.SetPermissions([], isAdmin: false));
        Assert.False(GetCanWrite(cut.Instance));

        // ...and restoring Write via a fresh event must drive it back to true.
        var writeProject = new List<EffectivePermissionDto>
        {
            new()
            {
                ResourceType = Aetheus.Shared.Components.Auth.ResourceType.Project,
                ResourceId = null,
                Permission = Aetheus.Shared.Components.Auth.Permission.Write
            }
        };
        await cut.InvokeAsync(() => permissions.SetPermissions(writeProject, isAdmin: false));
        Assert.True(GetCanWrite(cut.Instance));
    }

    // ── Test 9: DisposeAsync unsubscribes permissions ─────────────────────────

    [Fact]
    public async Task DisposeAsync_UnsubscribesPermissions()
    {
        SetupProjects();
        var cut = Render<GitRepositories>();
        var permissions = Services.GetRequiredService<PermissionService>();

        // While subscribed, a real permission event drives _canWrite: revoking Write flips it false.
        await cut.InvokeAsync(() => permissions.SetPermissions([], isAdmin: false));
        Assert.False(GetCanWrite(cut.Instance));

        await cut.Instance.DisposeAsync();

        // After dispose the handler is detached: restoring Write must NOT flip _canWrite back.
        var writeAll = Enum.GetValues<Aetheus.Shared.Components.Auth.ResourceType>()
            .Select(rt => new EffectivePermissionDto
            {
                ResourceType = rt,
                ResourceId = null,
                Permission = Aetheus.Shared.Components.Auth.Permission.Write
            })
            .ToList();
        permissions.SetPermissions(writeAll, isAdmin: false);
        Assert.False(GetCanWrite(cut.Instance));
    }

    // ── Test 10: Projects API error falls back to empty list ─────────────────

    [Fact]
    public void ProjectsApiError_FallsBackToEmpty()
    {
        _handler.SetResponse("api/projects", System.Net.HttpStatusCode.Unauthorized);
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        var projects = (List<ProjectDto>)typeof(GitRepositories).GetField("_projects", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(projects);
    }

    [Fact]
    public void MalformedProjectsResponse_PreservesExplicitProjectFilter()
    {
        _handler.SetRawResponse("api/projects", "<html>proxy error</html>");
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("git-repositories?projectId=77");

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => !cut.Instance.IsLoading);

        Assert.Equal(77, cut.Instance.ActiveProjectFilter);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("projectId=77", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadData_NewerRequestCancelsOlderResponseAndKeepsNewestRows()
    {
        SetupProjects();
        SetupRepos(1);
        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => !cut.Instance.IsLoading);

        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<PaginatedResult<GitLightRepoDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(
            HttpMethod.Get,
            "api/git/repos",
            async ct =>
            {
                oldStarted.TrySetResult();
                return await oldResponse.Task.WaitAsync(ct);
            });

        var oldLoad = cut.Instance.LoadData();
        await oldStarted.Task.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        _handler.SetJsonResponse(HttpMethod.Get, "api/git/repos", new PaginatedResult<GitLightRepoDto>
        {
            Items = [new GitLightRepoDto { Id = 99, Name = "newest" }],
            TotalCount = 1
        });
        await cut.Instance.LoadData();
        await oldLoad;

        Assert.Equal(99, Assert.Single(cut.Instance.Repositories).Id);
    }

    // ── Test 11: SyncUrlWithCurrentFilter does not crash ─────────────────────

    [Fact]
    public void SyncUrlWithCurrentFilter_NoFilter_NavigatesToBase()
    {
        SetupProjects();
        var cut = Render<GitRepositories>();

        var method = typeof(GitRepositories).GetMethod("SyncUrlWithCurrentFilter", Priv)!;
        method.Invoke(cut.Instance, []);

        // With no filter the page navigates to the bare list URL (no projectId query).
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.EndsWith("git-repositories", nav.Uri);
        Assert.DoesNotContain("projectId", nav.Uri);
    }

    // ── Test 12: SyncUrlWithCurrentFilter with filter includes projectId ──────

    [Fact]
    public void SyncUrlWithCurrentFilter_WithFilter_NavigatesToProjectUrl()
    {
        SetupProjects();
        SetupRepos(1);

        var cut = Render<GitRepositories>();
        cut.WaitForState(
            () => (bool)typeof(GitRepositories).GetField("_loading", Priv)!.GetValue(cut.Instance)! == false);

        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);
        var method = typeof(GitRepositories).GetMethod("SyncUrlWithCurrentFilter", Priv)!;
        method.Invoke(cut.Instance, []);

        // With filter = 1 the page navigates to the project-scoped URL carrying projectId=1.
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.EndsWith("git-repositories?projectId=1", nav.Uri);
    }
}
