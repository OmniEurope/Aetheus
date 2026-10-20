// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

// Covers global and project-scoped ReleasesList behaviour; server scope and badge mapping have
// dedicated test suites.
public class ReleasesListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type ListType = typeof(ReleasesList);

    public ReleasesListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        SetupDefaults();
    }

    private void SetupDefaults()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "MyProject", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items =
            [
                new ReleaseDto { Id = 1, ProjectId = 1, ProjectName = "MyProject", Version = "1.0.0", Status = ReleaseStatus.Published },
                new ReleaseDto { Id = 2, ProjectId = 1, ProjectName = "MyProject", Version = "1.1.0", Status = ReleaseStatus.Detected }
            ],
            TotalCount = 2
        });
    }

    // --- Render ---

    [Fact]
    public void GlobalScope_RendersReleases()
    {
        var cut = Render<ReleasesList>();
        cut.WaitForState(() => cut.Markup.Contains("1.0.0"));
        Assert.Contains("1.0.0", cut.Markup);
    }

    [Fact]
    public void ProjectScope_RendersReleases_WithoutProjectColumn()
    {
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 3, ProjectId = 7, ProjectName = "Scoped", Version = "2.1.0", Status = ReleaseStatus.Published }],
            TotalCount = 1
        });

        var cut = Render<ReleasesList>(p => p.Add(x => x.ProjectId, 7));
        cut.WaitForState(() => cut.Markup.Contains("2.1.0"));

        Assert.Contains("2.1.0", cut.Markup);
        // Project column is hidden in project scope, so the project name must not appear as a cell.
        Assert.DoesNotContain("Scoped", cut.Markup);
    }

    [Fact]
    public void ProjectScope_ReplacesChangelogWithRootPipelineLink()
    {
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items =
            [
                new ReleaseDto
                {
                    Id = 3,
                    ProjectId = 7,
                    Version = "2.1.0",
                    Status = ReleaseStatus.Published,
                    PipelineRunId = 99,
                    SourcePipelineId = 17,
                    SourcePipelineName = "release-orchestrator",
                    Changelog = "This text belongs in the detail view only."
                }
            ],
            TotalCount = 1
        });

        var cut = Render<ReleasesList>(parameters => parameters.Add(component => component.ProjectId, 7));
        cut.WaitForState(() => cut.Markup.Contains("release-orchestrator"));

        Assert.Contains("Pipeline", cut.Markup);
        Assert.DoesNotContain("Changelog", cut.Markup);
        Assert.Contains("href=\"/pipelines/17?projectId=7\"", cut.Markup);
        Assert.DoesNotContain("This text belongs in the detail view only.", cut.Markup);
    }

    [Fact]
    public void ServerScope_RootPipelineLinkPreservesServerContext()
    {
        _handler.SetJsonResponse("api/servers/4/releases", new PaginatedResult<ReleaseDto>
        {
            Items =
            [
                new ReleaseDto
                {
                    Id = 3,
                    ProjectId = 7,
                    Version = "2.1.0",
                    SourcePipelineId = 17,
                    SourcePipelineName = "release-orchestrator"
                }
            ],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });

        var cut = Render<ReleasesList>(parameters => parameters.Add(component => component.ServerId, 4));
        cut.WaitForState(() => cut.Markup.Contains("release-orchestrator"));

        Assert.Contains("href=\"/pipelines/17?serverId=4\"", cut.Markup);
    }

    [Fact]
    public void GlobalScope_EmptyReleases_StillCallsTheApi()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto> { Items = [], TotalCount = 0 });

        var cut = Render<ReleasesList>();
        cut.WaitForState(() => _handler.Requests.Any(r => r.Url.Contains("api/releases")));

        // Global scope loads its project filter list + releases even when empty; the empty result must
        // not surface any release version row.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/projects"));
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/releases"));
        Assert.DoesNotContain("1.0.0", cut.Markup);
    }

    // --- Behavior (reflection on the component) ---

    [Fact]
    public async Task OnLoadData_LoadsReleases()
    {
        var cut = Render<ReleasesList>();
        var method = ListType.GetMethod("OnLoadData", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new GridLoadArgs()])!);

        var releases = (List<ReleaseDto>)ListType.GetField("_releases", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(releases);
        Assert.False((bool)ListType.GetField("_loading", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task OnLoadData_WithSkipAndTop_PagesWithoutError()
    {
        var cut = Render<ReleasesList>();
        var method = ListType.GetMethod("OnLoadData", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new GridLoadArgs { Skip = 25, Top = 25 }])!);

        Assert.False((bool)ListType.GetField("_loading", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task ReleaseCreatedOutsideProjectScope_IsIgnored()
    {
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 1, ProjectId = 7, Version = "inside" }],
            TotalCount = 1
        });
        var cut = Render<ReleasesList>(parameters => parameters.Add(component => component.ProjectId, 7));
        var method = ListType.GetMethod("OnReleaseCreated", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance,
            [new ReleaseDto { Id = 99, ProjectId = 8, Version = "outside" }])!);

        var releases = (List<ReleaseDto>)ListType.GetField("_releases", Priv)!.GetValue(cut.Instance)!;
        Assert.DoesNotContain(releases, release => release.Id == 99);
    }

    [Fact]
    public async Task SyncReleases_WithNoFilter_LeavesTheFlagOff()
    {
        var cut = Render<ReleasesList>();
        ListType.GetField("_projectFilter", Priv)!.SetValue(cut.Instance, null);
        var requestsBeforeSync = _handler.Requests.Count;
        var method = ListType.GetMethod("SyncReleases", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.False((bool)ListType.GetField("_syncing", Priv)!.GetValue(cut.Instance)!);
        Assert.Equal(requestsBeforeSync, _handler.Requests.Count);
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Method == HttpMethod.Post.Method && request.Url.Contains("api/releases/sync/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncReleases_WithFilter_NewReleases_Completes()
    {
        const string syncUrl = "api/releases/sync/1";
        // The grid now sends its current sort with the page request, so the reload URL carries the
        // default ordering explicitly instead of leaving it implicit on the server.
        const string reloadUrl = "api/releases?page=1&pageSize=20&sortBy=PublishedAt&sortDescending=True&projectId=1";
        _handler.SetJsonResponse(HttpMethod.Post, syncUrl, new List<ReleaseDto>
        {
            new() { Id = 9, ProjectId = 1, Version = "1.2.0", Status = ReleaseStatus.Detected }
        });
        var cut = Render<ReleasesList>();
        cut.WaitForState(() => cut.Markup.Contains("1.0.0", StringComparison.Ordinal));
        var releaseGetsBeforeSync = _handler.Requests.Count(request =>
            request.Method == HttpMethod.Get.Method && request.Url.Contains("api/releases?", StringComparison.Ordinal));
        _handler.SetJsonResponse(HttpMethod.Get, reloadUrl, new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 9, ProjectId = 1, ProjectName = "MyProject", Version = "1.2.0", Status = ReleaseStatus.Detected }],
            TotalCount = 1,
            Page = 1,
            PageSize = 20
        });
        ListType.GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);
        var method = ListType.GetMethod("SyncReleases", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.False((bool)ListType.GetField("_syncing", Priv)!.GetValue(cut.Instance)!);
        Assert.Single(_handler.Requests, request =>
            request.Method == HttpMethod.Post.Method && request.Url == $"http://test/{syncUrl}");
        Assert.True(_handler.Requests.Count(request =>
            request.Method == HttpMethod.Get.Method && request.Url.Contains("api/releases?", StringComparison.Ordinal))
            > releaseGetsBeforeSync);
        cut.WaitForAssertion(() => Assert.Contains("1.2.0", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(Services.Toasts(), message =>
            message.Severity == OmniSeverity.Success
            && message.Summary == "Synced"
            && message.Detail.Contains("ReleasesSynced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearFilters_ResetsProjectFilter()
    {
        var cut = Render<ReleasesList>();
        ListType.GetField("_projectFilter", Priv)!.SetValue(cut.Instance, 1);
        var method = ListType.GetMethod("ClearFilters", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.Null((int?)ListType.GetField("_projectFilter", Priv)!.GetValue(cut.Instance));
    }

    [Fact]
    public void RefreshCanWrite_SetsFlag()
    {
        var cut = Render<ReleasesList>();
        ListType.GetMethod("RefreshCanWrite", Priv)!.Invoke(cut.Instance, []);

        Assert.True((bool)ListType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public void OnPermissionsChanged_UpdatesCanWrite()
    {
        var cut = Render<ReleasesList>();
        ListType.GetField("_canWrite", Priv)!.SetValue(cut.Instance, false);
        ListType.GetMethod("OnPermissionsChanged", Priv)!.Invoke(cut.Instance, []);

        Assert.True((bool)ListType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task TriggerBuild_NoPipelines_StillCallsTheApi()
    {
        _handler.SetJsonResponse("api/pipelines", new PaginatedResult<PipelineDto> { Items = [], TotalCount = 0 });
        var cut = Render<ReleasesList>();
        var release = new ReleaseDto { Id = 1, Version = "1.0.0", Status = ReleaseStatus.Detected, ProjectName = "App" };
        var method = ListType.GetMethod("TriggerBuild", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [release])!);

        // TriggerBuild fetches the candidate pipelines first; with none it warns and returns before
        // opening the select dialog or POSTing a build trigger.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/pipelines"));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("/build"));
    }

    [Fact]
    public async Task DisposeAsync_Completes()
    {
        var cut = Render<ReleasesList>();
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
    }

    // --- S-TECH-B4H9: realtime wiring (#8) - project scope reuses ProjectDetailLoader.OnChanged ---

    [Fact]
    public void ProjectScope_SubscribesToProjectLoaderOnChanged()
    {
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        // No subscriber before any project-scoped list mounts.
        Assert.Null(GetOnChangedHandler(loader));

        var cut = Render<ReleasesList>(p => p.Add(x => x.ProjectId, 7));
        cut.WaitForState(() => cut.Markup.Contains("1.0.0") || cut.Markup.Contains("NoReleasesFound"));

        // Project scope subscribed to the loader's OnChanged (the reused releases hub path).
        Assert.NotNull(GetOnChangedHandler(loader));
    }

    [Fact]
    public async Task ProjectScope_ProjectLoaderOnChanged_ReloadsGridWithNewData()
    {
        var cut = Render<ReleasesList>(p => p.Add(x => x.ProjectId, 7));
        cut.WaitForState(() => cut.Markup.Contains("1.0.0"));

        // A new release arrives; raising the loader's OnChanged must re-page the grid and surface it.
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 50, ProjectId = 7, Version = "9.9.9", Status = ReleaseStatus.Published }],
            TotalCount = 1
        });
        var handler = GetOnChangedHandler(Services.GetRequiredService<ProjectDetailLoader>());
        Assert.NotNull(handler);
        await cut.InvokeAsync(() => handler!.Invoke());

        cut.WaitForState(() => cut.Markup.Contains("9.9.9"));
        Assert.Contains("9.9.9", cut.Markup);
    }

    [Fact]
    public async Task HeaderFilters_AreSentAsColumnFilters_WithNamesFromTheApi()
    {
        // Dates, status and grade lists, project and pipeline lists read from the API.
        _handler.SetJsonResponse("api/releases/filter-values", new ReleaseFilterValuesDto
        {
            ProjectNames = ["MyProject"],
            SourcePipelineNames = ["build"]
        });
        var cut = Render<ReleasesList>();
        var grid = cut.FindComponent<AetheusDataGrid<ReleaseDto>>();
        var separator = Aetheus.Shared.Components.Shared.GridFilter.ListSeparator;

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(ReleaseDto.PublishedAt), "2026-09-01",
                    OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.LessThan, "2026-09-10"),
                new GridFilterDescriptor(nameof(ReleaseDto.Status), $"Published{separator}Deployed", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(ReleaseDto.SourcePipelineName), "build", OmniDataGridFilterOperator.In)
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/releases?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=PublishedAt", StringComparison.Ordinal)
                && url.Contains("Filters[0].SecondValue=2026-09-10", StringComparison.Ordinal)
                && url.Contains($"Filters[1].Value=Published{separator}Deployed", StringComparison.Ordinal)
                && url.Contains("Filters[2].Field=SourcePipelineName", StringComparison.Ordinal);
        }));
        var values = (ReleaseFilterValuesDto)ListType.GetField("_filterValues", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(["build"], values.SourcePipelineNames);
    }

    private static Action? GetOnChangedHandler(ProjectDetailLoader loader) =>
        (Action?)typeof(ProjectDetailLoader).GetField("OnChanged", Priv)!.GetValue(loader);
}
