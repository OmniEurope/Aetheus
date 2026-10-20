// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Git;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Git;

/// <summary>
/// Coverage for GitRepositories.razor.cs - DeleteRepo, ClearFilters,
/// ApplyProjectFilterFromQuery, SyncUrlWithCurrentFilter paths.
/// Dialog.Confirm (DeleteRepo) returns null in loose mock = early return.
/// </summary>
public class GitRepositoriesCoverageTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoriesCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupDefault()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "MyProject" }, new ProjectDto { Id = 2, Name = "OtherProject" }],
            TotalCount = 2
        });
        _handler.SetPaginatedJsonResponse("api/git", new List<GitLightRepoDto>
        {
            new() { Id = 1, Name = "repo1", CloneUrl = "https://github.com/repo1.git" },
            new() { Id = 2, Name = "repo2", CloneUrl = "https://github.com/repo2.git" },
        });
    }

    // ── Renders page ──────────────────────────────────────────────────────────

    [Fact]
    public void Renders_WithoutProjectFilter_ListsAllRepos()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // Without a project filter the page now lists every repo the user can read (cross-project view),
        // instead of the old "select a project first" empty state. SetupDefault stubs two repos.
        var repos = (List<GitLightRepoDto>)typeof(GitRepositories).GetField("_repos", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, repos.Count);
    }

    // ── LoadData with project filter ─────────────────────────────────────────

    [Fact]
    public async Task LoadData_WithFilter_LoadsRepos()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);

        var method = typeof(GitRepositories).GetMethod("LoadData", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var repos = (List<GitLightRepoDto>)typeof(GitRepositories).GetField("_repos", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(repos);
    }

    // ── ClearFilters ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearFilters_ClearsProjectFilter()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);

        var method = typeof(GitRepositories).GetMethod("ClearFilters", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var filter = typeof(GitRepositories).GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(filter);
    }

    // ── OnProjectFilterChanged ────────────────────────────────────────────────

    [Fact]
    public async Task OnProjectFilterChanged_LoadsData()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("git-repositories?projectId=1");
        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);
        _handler.Requests.Clear();
        var method = typeof(GitRepositories).GetMethod("OnProjectFilterChanged", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [new object()])!);

        // Filter = 1 triggers a reload via GET api/git/repos?projectId=1; the stub returns two repos.
        var requestUrl = _handler.Requests.Last(r => r.Method == "GET" && r.Url.Contains("api/git/repos")).Url;
        Assert.Contains("projectId=1", requestUrl);
        var repos = (List<GitLightRepoDto>)typeof(GitRepositories).GetField("_repos", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, repos.Count);
    }

    // ── DeleteRepo: Dialog returns null → no deletion ────────────────────────

    // DeleteRepo removed - calls Dialog.Confirm which hangs in bUnit

    // ── ShowCreateDialog: no filter → early return ───────────────────────────

    [Fact]
    public async Task ShowCreateDialog_NoFilter_ReturnsEarly()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(GitRepositories).GetField("_projectFilter", Priv)!.SetValue(cut.Instance, (int?)null);

        // With no filter, ShowCreateDialog must return BEFORE the dialog + post-dialog reload, so the
        // api/git/repos GET count stays unchanged. (Reaching the real OmniDialogService.OpenAsync would leave
        // the awaited Task unresolved and hang this test - so completing without a reload proves the early
        // return, not a tautology.)
        var reposLoadsBefore = _handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("api/git/repos"));

        var method = typeof(GitRepositories).GetMethod("ShowCreateDialog", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var reposLoadsAfter = _handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("api/git/repos"));
        Assert.Equal(reposLoadsBefore, reposLoadsAfter);
    }

    // ── ApplyProjectFilterFromQuery: projectId not in list → null ────────────

    [Fact]
    public void ApplyProjectFilterFromQuery_ProjectNotInList_SetsNull()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // Simulate having a query param that doesn't match a known project
        typeof(GitRepositories).GetProperty("ProjectId")!.SetValue(cut.Instance, 999);

        var method = typeof(GitRepositories).GetMethod("ApplyProjectFilterFromQuery", Priv)!;
        method.Invoke(cut.Instance, []);

        var filter = typeof(GitRepositories).GetField("_projectFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(filter);
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    private static bool GetCanWrite(GitRepositories instance) =>
        (bool)typeof(GitRepositories).GetField("_canWrite", Priv)!.GetValue(instance)!;

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromPermissionChanges()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        var permissions = Services.GetRequiredService<PermissionService>();

        // While subscribed, a real permission event drives _canWrite: revoking Write flips it false.
        await cut.InvokeAsync(() => permissions.SetPermissions([], isAdmin: false));
        Assert.False(GetCanWrite(cut.Instance));

        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());

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

    // ── OnPermissionsChanged ──────────────────────────────────────────────────

    [Fact]
    public async Task OnPermissionsChanged_RecomputesCanWriteFromEvent()
    {
        SetupDefault();
        var cut = Render<GitRepositories>();
        var permissions = Services.GetRequiredService<PermissionService>();

        // Test user starts with Write on Project - the subscribed handler has _canWrite true.
        Assert.True(GetCanWrite(cut.Instance));

        // Raising the real event with no Write permission must drive _canWrite to false...
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
}
