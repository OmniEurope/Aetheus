// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// Render + method coverage for ProjectDetailSections not yet fully covered:
/// - ProjectOverviewSection: recent pipeline/commit loading and repeated parameter sets
/// - ProjectLibrariesSection: NewLibrary navigation + OnInitializedAsync result
/// - ProjectVaultsSection: NewVault navigation + field read
/// - ProjectServersSection: GetTypeBadge + GetStatusBadge + EnsureAgentServersAsync
/// - ProjectEditSection: OnParametersSet branches (null project, Active/Archived status options)
/// - ProjectTasksSection: server-side paging loads the requested page
/// - ProjectReleasesSection: GetReleaseBadge for all status enum values
/// </summary>
public class ProjectDetailSectionsRenderTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDetailSectionsRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(
            HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=",
            []);
    }

    // ── ProjectOverviewSection ────────────────────────────────────────────────

    [Fact]
    public void ProjectOverviewSection_NullProject_DoesNotLoadRecentData()
    {
        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, (ProjectDetailDto?)null));
        var pipelines = (List<PipelineRunDto>)typeof(ProjectOverviewSection)
            .GetField("_recentPipelineRuns", Priv)!.GetValue(cut.Instance)!;
        var commits = (System.Collections.ICollection)typeof(ProjectOverviewSection)
            .GetField("_recentCommits", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(pipelines);
        Assert.Empty(commits);
    }

    [Fact]
    public void ProjectOverviewSection_WithProject_LoadingFlagFalseAfterInit()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto { Id = 1, Name = "Web" }));
        cut.WaitForState(() =>
        {
            var pipelinesLoading = (bool)typeof(ProjectOverviewSection)
                .GetField("_loadingRecentPipelines", Priv)!.GetValue(cut.Instance)!;
            var commitsLoading = (bool)typeof(ProjectOverviewSection)
                .GetField("_loadingRecentCommits", Priv)!.GetValue(cut.Instance)!;
            return !pipelinesLoading && !commitsLoading;
        });
    }

    [Fact]
    public void ProjectOverviewSection_SecondParameterSet_DoesNotReloadRecentData()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var project = new ProjectDetailDto { Id = 2, Name = "API" };
        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, project));
        cut.WaitForState(() =>
        {
            var loading = (bool)typeof(ProjectOverviewSection)
                .GetField("_loadingRecentPipelines", Priv)!.GetValue(cut.Instance)!;
            return !loading;
        });
        // Set parameters again - recent data endpoints must NOT be hit a second time.
        cut.Render(p => p.Add(x => x.Project, project));
        var pipelineCalls = _handler.Requests.Count(r =>
            r.Method == "GET" && r.Url.Contains("api/pipelines/runs/recent?projectId=2"));
        var repositoryCalls = _handler.Requests.Count(r =>
            r.Method == "GET"
            && r.Url.Contains("api/git/repos?", StringComparison.Ordinal)
            && r.Url.Contains("projectId=2", StringComparison.Ordinal));
        Assert.Equal(1, pipelineCalls);
        Assert.Equal(1, repositoryCalls);
    }

    // ProjectLibrariesSection was replaced by the shared VariableLibrariesList (project scope) -
    // its render / new-library / empty coverage now lives in VariableLibrariesListTests.

    // ProjectVaultsSection was replaced by the shared VaultsList (project scope) - its render /
    // new-vault coverage now lives in VaultsListTests.

    // ── ProjectServersSection badges ──────────────────────────────────────────

    [Theory]
    [InlineData(ProjectServerType.AgentServer, OmniTone.Accent)]
    [InlineData(ProjectServerType.ExternalHost, OmniTone.Warning)]
    public void ProjectServersSection_GetTypeBadge_ReturnsExpected(ProjectServerType type, OmniTone expected)
    {
        var method = typeof(ProjectServersSection).GetMethod("GetTypeBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)type])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ProjectServersSection_GetTypeBadge_Unknown_ReturnsLight()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetTypeBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)(ProjectServerType)99])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    [Theory]
    [InlineData(ServerStatus.Online, OmniTone.Success)]
    [InlineData(ServerStatus.Offline, OmniTone.Danger)]
    [InlineData(ServerStatus.Disabled, OmniTone.Neutral)]
    public void ProjectServersSection_GetStatusBadge_KnownStatuses(ServerStatus status, OmniTone expected)
    {
        var method = typeof(ProjectServersSection).GetMethod("GetStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object?)status])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ProjectServersSection_GetStatusBadge_Null_ReturnsLight()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object?)null])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    // ── ProjectServersSection.EnsureAgentServersAsync ─────────────────────────

    [Fact]
    public async Task ProjectServersSection_EnsureAgentServersAsync_LoadsServers()
    {
        _handler.SetPaginatedJsonResponse("api/projects/30/servers", new List<ProjectServerDto>());
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items =
            [
                new ServerDto { Id = 1, Name = "prod-01", Type = ServerType.Normal, Status = ServerStatus.Online, Tags = [] }
            ],
            TotalCount = 1
        });

        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 30));

        var method = typeof(ProjectServersSection).GetMethod("EnsureAgentServersAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var servers = (List<ServerDto>?)typeof(ProjectServersSection).GetField("_agentServers", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(servers);
        Assert.Single(servers!);
    }

    [Fact]
    public async Task ProjectServersSection_EnsureAgentServersAsync_SecondCall_DoesNotReload()
    {
        _handler.SetPaginatedJsonResponse("api/projects/31/servers", new List<ProjectServerDto>());
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 1, Name = "srv", Type = ServerType.Normal, Tags = [] }],
            TotalCount = 1
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 31));

        var method = typeof(ProjectServersSection).GetMethod("EnsureAgentServersAsync", Priv)!;
        // First call loads
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);
        var firstCount = ((List<ServerDto>?)typeof(ProjectServersSection).GetField("_agentServers", Priv)!.GetValue(cut.Instance))?.Count ?? 0;

        // Modify stub to return different data - second call should use cached value
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [
                new ServerDto { Id = 1, Name = "srv", Type = ServerType.Normal, Tags = [] },
                new ServerDto { Id = 2, Name = "srv2", Type = ServerType.Normal, Tags = [] }
            ],
            TotalCount = 2
        });
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);
        var secondCount = ((List<ServerDto>?)typeof(ProjectServersSection).GetField("_agentServers", Priv)!.GetValue(cut.Instance))?.Count ?? 0;

        // Should still be the cached count (not 2)
        Assert.Equal(firstCount, secondCount);
    }

    // ── ProjectEditSection ────────────────────────────────────────────────────

    [Fact]
    public void ProjectEditSection_NullProject_StatusOptionsStillPopulated()
    {
        var cut = Render<ProjectEditSection>(p =>
            p.Add(x => x.Project, (ProjectDetailDto?)null));
        var opts = (System.Collections.IList)typeof(ProjectEditSection)
            .GetField("_statusOptions", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, opts.Count);
    }

    [Fact]
    public void ProjectEditSection_WithArchivedProject_StatusPopulatedFromProject()
    {
        var project = new ProjectDetailDto
        {
            Id = 7,
            Name = "Old App",
            Description = "archived",
            Status = ProjectStatus.Archived,
            Tags = []
        };
        var cut = Render<ProjectEditSection>(p => p.Add(x => x.Project, project));
        var model = typeof(ProjectEditSection).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var status = (ProjectStatus)model.GetType().GetProperty("Status")!.GetValue(model)!;
        Assert.Equal(ProjectStatus.Archived, status);
    }

    [Fact]
    public async Task ProjectEditSection_OnSubmit_WithEmptyTags_SendsEmptyList()
    {
        var project = new ProjectDetailDto { Id = 3, Name = "Test", Status = ProjectStatus.Active, Tags = [] };
        _handler.SetJsonResponse("api/projects/3", new ProjectDetailDto { Id = 3, Name = "Test", Tags = [] });

        var savedCalled = false;
        var cut = Render<ProjectEditSection>(p =>
            p.Add(x => x.Project, project)
             .Add(x => x.OnSaved, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => savedCalled = true)));

        // Empty tags raw
        var model = typeof(ProjectEditSection).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("TagsRaw")!.SetValue(model, "");
        model.GetType().GetProperty("Name")!.SetValue(model, "Test");

        var method = typeof(ProjectEditSection).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.True(savedCalled);
    }

    // ProjectReleasesSection.GetReleaseBadge moved to the shared Components/Releases/ReleaseHelper - its badge
    // mapping is covered directly by ReleaseHelperTests.
}
