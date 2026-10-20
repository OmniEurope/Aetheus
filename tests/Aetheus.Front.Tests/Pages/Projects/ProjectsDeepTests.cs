// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// Deep coverage for Projects.razor.cs - LoadDataAndRender, ClearFilters,
/// TruncateText, StartHubAsync, OnPermissionsChanged, DisposeAsync.
/// </summary>
public class ProjectsDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectsDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupProjects(int count = 3)
    {
        var items = Enumerable.Range(1, count).Select(i => new ProjectDto
        {
            Id = i,
            Name = $"Project {i}",
            Status = ProjectStatus.Active,
            Tags = []
        }).ToList();

        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = items,
            TotalCount = count
        });
    }

    // ── OnInitialized - loads data ────────────────────────────────────────────

    [Fact]
    public void OnInit_LoadsProjects()
    {
        SetupProjects();
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var projects = (List<ProjectDto>)typeof(ProjectsPage).GetField("_projects", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(3, projects.Count);
    }

    // ── LoadDataAndRender ──────────────────────────────────────────────────────

    [Fact]
    public async Task LoadDataAndRender_RefreshesProjects()
    {
        SetupProjects(2);
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(ProjectsPage).GetMethod("LoadDataAndRender", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var projects = (List<ProjectDto>)typeof(ProjectsPage).GetField("_projects", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, projects.Count);
    }

    // ── ClearFilters - resets search and status ───────────────────────────────

    [Fact]
    public async Task ClearFilters_ResetsFilters()
    {
        SetupProjects();
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(ProjectsPage).GetField("_search", Priv)!.SetValue(cut.Instance, "some search");
        typeof(ProjectsPage).GetField("_statusFilter", Priv)!.SetValue(cut.Instance, (ProjectStatus?)ProjectStatus.Active);

        var method = typeof(ProjectsPage).GetMethod("ClearFilters", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var search = (string?)typeof(ProjectsPage).GetField("_search", Priv)!.GetValue(cut.Instance);
        var status = (ProjectStatus?)typeof(ProjectsPage).GetField("_statusFilter", Priv)!.GetValue(cut.Instance);
        Assert.Null(search);
        Assert.Null(status);
    }

    // ── TruncateText static method ────────────────────────────────────────────

    [Fact]
    public void TruncateText_ShortText_NoTruncation()
    {
        var method = typeof(ProjectsPage).GetMethod("TruncateText", Stat)!;
        var result = (string)method.Invoke(null, ["Hello", 20])!;
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void TruncateText_LongText_Truncates()
    {
        var method = typeof(ProjectsPage).GetMethod("TruncateText", Stat)!;
        var result = (string)method.Invoke(null, ["This is a very long text", 10])!;
        Assert.Equal("This is...", result);
    }

    // ── OnPermissionsChanged ──────────────────────────────────────────────────

    [Fact]
    public void OnPermissionsChanged_SetsCanWrite()
    {
        SetupProjects();
        var cut = Render<ProjectsPage>();
        // Force _canWrite to a known-wrong value, then let OnPermissionsChanged recompute it.
        typeof(ProjectsPage).GetField("_canWrite", Priv)!.SetValue(cut.Instance, false);

        var method = typeof(ProjectsPage).GetMethod("OnPermissionsChanged", Priv)!;
        method.Invoke(cut.Instance, []);

        // The test PermissionService grants Read+Write on every resource type, so the
        // recompute must flip _canWrite back to true.
        var canWrite = (bool)typeof(ProjectsPage).GetField("_canWrite", Priv)!.GetValue(cut.Instance)!;
        Assert.True(canWrite);
    }

    // ── StartHubAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StartHubAsync_SwallowsHubFailure_AndAssignsConnection()
    {
        SetupProjects();
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(ProjectsPage).GetMethod("StartHubAsync", Priv)!;

        // StartAsync fails against the null hub factory; StartHubAsync must swallow it (no throw).
        var ex = await Record.ExceptionAsync(() =>
            cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!));
        Assert.Null(ex);

        // The connection is assigned before StartAsync, so the field is populated even on failure.
        var hub = typeof(ProjectsPage).GetField("_hubConnection", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(hub);
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_Succeeds()
    {
        SetupProjects();
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        await ((IAsyncDisposable)cut.Instance).DisposeAsync();

        // DisposeAsync tears down and nulls the hub connection that OnInitializedAsync created.
        var hub = typeof(ProjectsPage).GetField("_hubConnection", Priv)!.GetValue(cut.Instance);
        Assert.Null(hub);
    }

    // ── LoadData with search/status ───────────────────────────────────────────

    [Fact]
    public async Task LoadData_WithSearchFilter()
    {
        SetupProjects();
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(ProjectsPage).GetField("_search", Priv)!.SetValue(cut.Instance, "Project 1");
        var method = typeof(ProjectsPage).GetMethod("LoadData", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // LoadData forwards _search to GetProjectsAsync, which encodes it into the query string.
        Assert.Contains(_handler.Requests,
            r => r.Method == "GET" && r.Url.Contains("search=Project", StringComparison.OrdinalIgnoreCase));
    }

    // ── Status options built on init ──────────────────────────────────────────

    [Fact]
    public void StatusOptions_ArePopulated()
    {
        SetupProjects();
        var cut = Render<ProjectsPage>();
        var options = (System.Collections.IList)typeof(ProjectsPage).GetField("_statusOptions", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, options.Count);
    }
}
