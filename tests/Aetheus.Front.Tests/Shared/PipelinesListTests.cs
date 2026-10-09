// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

public class PipelinesListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private IRenderedComponent<ContainerFragment>? _host;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public PipelinesListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
    }

    [Fact]
    public void FavoriteState_IsLoadedAndForwardedToPipelineGrid()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
            [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto { PipelineIds = [7] });

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("RemovePipelineFromFavorites", cut.Markup);
            // R-119: no favourites panel any more; the favourite is a row of the one catalogue grid.
            Assert.Empty(cut.FindAll(".pipeline-favorites-section"));
            var grid = Assert.Single(cut.FindComponents<PipelineDependencyGrid>()).Instance;
            Assert.Equal(["Build"], grid.Items.Select(item => item.Name));
            Assert.Contains(7, grid.FavoritePipelineIds);
        }, TimeSpan.FromSeconds(2));
    }

    /// <summary>R-119: the starred pipelines lead the one table, the rest keep project then name order.</summary>
    [Fact]
    public void Favorites_AreSortedFirstInTheSingleCatalogue()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(
            parents: [new PipelineDependencyDto { Id = 1, Name = "alpha", References = [new PipelineDependencyReferenceDto(3, "zulu")] }],
            leaves:
            [
                new PipelineDependencyDto { Id = 2, Name = "bravo" },
                new PipelineDependencyDto { Id = 3, Name = "zulu", Parents = [new PipelineDependencyReferenceDto(1, "alpha")] }
            ]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto { PipelineIds = [3] });

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            var grid = Assert.Single(cut.FindComponents<PipelineDependencyGrid>()).Instance;
            Assert.Equal(["zulu", "alpha", "bravo"], grid.Items.Select(item => item.Name));
        }, TimeSpan.FromSeconds(2));
        Assert.Empty(cut.FindAll(".pipeline-favorites-section"));
        Assert.DoesNotContain("FavoritePipelinesHint", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>R-121: the "with children" toggle keeps only the rows that launch other pipelines.</summary>
    [Fact]
    public void WithChildrenFilter_KeepsOnlyParentRows()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(
            parents: [new PipelineDependencyDto { Id = 1, Name = "release", References = [new PipelineDependencyReferenceDto(2, "deploy")] }],
            leaves: [new PipelineDependencyDto { Id = 2, Name = "deploy" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList();
        cut.WaitForState(() => cut.FindComponents<PipelineDependencyGrid>().Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(2, cut.FindComponent<PipelineDependencyGrid>().Instance.Items.Count);

        typeof(PipelinesList).GetField("_withChildrenOnly", Priv)!.SetValue(cut.Instance, true);
        cut.Render();

        Assert.Equal(["release"], cut.FindComponent<PipelineDependencyGrid>().Instance.Items.Select(item => item.Name));
    }

    /// <summary>R-122: each chip counts from the loaded data, and a click filters the table on it.</summary>
    [Fact]
    public void StatusSummary_CountsFromLoadedData_AndClickFilters()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
        [
            new PipelineDependencyDto { Id = 1, Name = "broken", RecentRuns = [Run(11, PipelineStatus.Failed), Run(10, PipelineStatus.Success)] },
            new PipelineDependencyDto { Id = 2, Name = "busy", RecentRuns = [Run(21, PipelineStatus.WaitingForApproval)] },
            new PipelineDependencyDto { Id = 3, Name = "fresh" },
            new PipelineDependencyDto { Id = 4, Name = "healed", RecentRuns = [Run(41, PipelineStatus.Success), Run(40, PipelineStatus.Failed)] }
        ]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>
        {
            TotalCount = 1,
            Items = [new() { PipelineId = 4, TemplateId = 3, TemplateName = "ci", Freshness = PipelineFleetFreshness.Outdated, PinnedVersion = 1, LatestVersion = 2 }]
        });

        var cut = RenderList();
        // Recette R-167: the three fleet freshness states each have their chip.
        cut.WaitForState(() => cut.FindAll(".pipeline-status-chip").Count == 6, TimeSpan.FromSeconds(2));

        string Chip(string status) => cut.Find($".pipeline-status-chip-{status}").TextContent;
        Assert.Contains("Failed", Chip("failed"), StringComparison.Ordinal);
        Assert.Contains("1", cut.Find(".pipeline-status-chip-failed .pipeline-status-chip-count").TextContent, StringComparison.Ordinal);
        Assert.Equal("1", cut.Find(".pipeline-status-chip-running .pipeline-status-chip-count").TextContent.Trim());
        Assert.Equal("1", cut.Find(".pipeline-status-chip-neverrun .pipeline-status-chip-count").TextContent.Trim());
        Assert.Equal("1", cut.Find(".pipeline-status-chip-outdated .pipeline-status-chip-count").TextContent.Trim());
        Assert.Contains("PipelineNeverRun", Chip("neverrun"), StringComparison.Ordinal);
        Assert.Equal(4, cut.FindComponent<PipelineDependencyGrid>().Instance.Items.Count);

        cut.Find(".pipeline-status-chip-failed").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["broken"], cut.FindComponent<PipelineDependencyGrid>().Instance.Items.Select(item => item.Name));
            Assert.Equal("true", cut.Find(".pipeline-status-chip-failed").GetAttribute("aria-pressed"));
        });

        // Recette R-167: a second chip adds its rows (any chosen state matches).
        cut.Find(".pipeline-status-chip-neverrun").Click();
        cut.WaitForAssertion(() => Assert.Equal(["broken", "fresh"],
            cut.FindComponent<PipelineDependencyGrid>().Instance.Items.Select(item => item.Name).Order()));

        // Clicking each again lifts its filter.
        cut.Find(".pipeline-status-chip-failed").Click();
        cut.Find(".pipeline-status-chip-neverrun").Click();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindComponent<PipelineDependencyGrid>().Instance.Items.Count));
    }

    /// <summary>R-120: without a ?tab in the URL, the tab this user last opened comes back.</summary>
    [Fact]
    public void RememberedRunsTab_IsRestored_WhenTheUrlNamesNoTab()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves: [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>
        {
            new() { Id = 12, PipelineId = 7, PipelineName = "Build", Status = PipelineStatus.Success }
        });
        JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("runs");

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindComponents<PipelineRunsGrid>());
            Assert.Empty(cut.FindComponents<PipelineDependencyGrid>());
            Assert.Contains(">#12<", cut.Markup, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void GlobalHeader_KeepsCreateVisible_AndPutsImportInTheMoreActionsMenu()
    {
        // Recette R-412 (user decision 2026-09-28): Create stays a visible blue button, Import moves
        // into the header's "..." menu.
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves: [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        RenderList();
        var host = _host!;

        var create = host.WaitForElement(".pipeline-list-new-button", TimeSpan.FromSeconds(2));
        Assert.Contains("omni-button--primary", create.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain(host.FindAll("button"), button =>
            button.TextContent.Contains("Import", StringComparison.Ordinal)
            && !button.ClassList.Contains("omni-menu__item"));

        host.Find(".omni-overflow-menu__trigger").Click();

        var item = Assert.Single(host.FindAll(".omni-overflow-menu__popup .omni-menu__item"));
        Assert.Contains("Import", item.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitUrlTab_WinsOverTheRememberedTab()
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/pipelines?tab=catalog");
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves: [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("runs");

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindComponents<PipelineDependencyGrid>());
            Assert.Empty(cut.FindComponents<PipelineRunsGrid>());
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ChoosingATab_RemembersItForThisUser()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves: [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList();
        cut.WaitForState(() => ViewItems().Count == 2, TimeSpan.FromSeconds(2));

        ViewItems()[1].Click();

        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "localStorage.setItem"
            && invocation.Arguments[0] is string key && key.StartsWith("aetheus.pipelines.view-tab.", StringComparison.Ordinal)
            && Equals(invocation.Arguments[1], "runs")));
    }

    private static PipelineRunSummaryDto Run(int id, PipelineStatus status) => new() { Id = id, Status = status };

    [Fact]
    public void TemplateFilter_ShowsOnlyPipelinesUsingSelectedModel()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
        [
            new PipelineDependencyDto { Id = 7, Name = "Build" },
            new PipelineDependencyDto { Id = 8, Name = "Deploy" }
        ]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>
        {
            TotalCount = 2,
            Items =
            [
                new() { PipelineId = 7, TemplateId = 3, TemplateName = "ci", Freshness = PipelineFleetFreshness.Current },
                new() { PipelineId = 8, TemplateId = 4, TemplateName = "delivery", Freshness = PipelineFleetFreshness.Current }
            ]
        });

        var cut = RenderList(templateId: 3);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Build", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(">Deploy<", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("href=\"/templates/3\"", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AutonomousFilter_ShowsOnlyPipelinesWithoutModel()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
        [
            new PipelineDependencyDto { Id = 7, Name = "Build" },
            new PipelineDependencyDto { Id = 8, Name = "Standalone" }
        ]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>
        {
            TotalCount = 2,
            Items =
            [
                new() { PipelineId = 7, TemplateId = 3, TemplateName = "ci", Freshness = PipelineFleetFreshness.Current },
                new() { PipelineId = 8, Freshness = PipelineFleetFreshness.OffCatalog }
            ]
        });

        var cut = RenderList(templateId: -1);

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(">Build<", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Standalone", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("AutonomousPipeline", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FavoriteButton_ClickPersistsAndRendersFilledStarWithoutNetworkListener()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
            [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse(
            HttpMethod.Put,
            "api/pipelines/7/favorite",
            new PipelineFavoriteDto { PipelineId = 7, IsFavorite = true });
        var cut = RenderList();
        var button = cut.WaitForElement(
            "button[aria-label='AddPipelineToFavorites']",
            TimeSpan.FromSeconds(2));

        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_handler.Requests, request =>
                request.Method == HttpMethod.Put.Method
                && request.Url.Contains("api/pipelines/7/favorite", StringComparison.Ordinal));
            Assert.Contains("RemovePipelineFromFavorites", cut.Markup);
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ServerScope_UsesDependencyAndRecentTables_AndPreservesServerLinks()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
        [
            new PipelineDependencyDto
            {
                Id = 7,
                Name = "Server pipeline",
                TriggerType = PipelineTriggerType.Manual,
                RecentRuns = [new PipelineRunSummaryDto { Id = 71, Status = PipelineStatus.Success }]
            }
        ]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>
        {
            new() { Id = 71, PipelineId = 7, PipelineName = "Server pipeline", Status = PipelineStatus.Success }
        });

        var cut = RenderList(serverId: 42);
        cut.WaitForState(() => cut.Markup.Contains("Server pipeline"), TimeSpan.FromSeconds(2));

        Assert.Contains(_handler.Requests, request => request.Url.Contains("dependencies?serverId=42"));
        Assert.Contains(_handler.Requests, request => request.Url.Contains("runs/recent?serverId=42"));
        Assert.Contains("href=\"/pipelines/7?serverId=42\"", cut.Markup);
        Assert.Single(cut.FindComponents<PipelineDependencyGrid>());
        OpenRunsTab(cut);
        cut.WaitForAssertion(() => Assert.Contains("href=\"/pipelines/runs/71?serverId=42\"", cut.Markup));
        Assert.Equal(42, cut.FindComponent<PipelineRunsGrid>().Instance.ServerId);
    }

    [Fact]
    public void ProjectScope_PreservesProjectLinks()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
            [new PipelineDependencyDto { Id = 8, Name = "Project pipeline", ProjectId = 3, TriggerType = PipelineTriggerType.Webhook }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>
        {
            new() { Id = 14, PipelineId = 8, PipelineName = "Project pipeline", Status = PipelineStatus.Success }
        });

        var cut = RenderList(projectId: 3);
        cut.WaitForState(() => cut.Markup.Contains("Project pipeline"), TimeSpan.FromSeconds(2));

        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/pipelines/dependencies"));
        Assert.Contains(_handler.Requests, request => request.Url.Contains("runs/recent?projectId=3"));
        Assert.Contains("href=\"/pipelines/8?projectId=3\"", cut.Markup);
        Assert.Contains("SearchPipelines", cut.Markup);
        Assert.Contains("FavoritesOnly", cut.Markup);
        Assert.DoesNotContain(">Type<", cut.Markup);
        Assert.DoesNotContain("PipelineReferences", cut.Markup);
        OpenRunsTab(cut);
        cut.WaitForAssertion(() => Assert.Contains(">#14<", cut.Markup));
    }

    [Fact]
    public void ProjectScope_RendersUnifiedWorkspaceAndFiltersImmediately()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
        [
            new PipelineDependencyDto { Id = 8, Name = "Build", ProjectId = 3, TriggerType = PipelineTriggerType.Manual },
            new PipelineDependencyDto { Id = 9, Name = "Deploy", ProjectId = 3, TriggerType = PipelineTriggerType.Webhook }
        ]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto { PipelineIds = [8] });

        var cut = RenderList(projectId: 3);
        // R-118/R-120: one catalogue grid (no favourites grid above it), the runs behind their own tab.
        cut.WaitForState(() => cut.FindComponents<PipelineDependencyGrid>().Count == 1, TimeSpan.FromSeconds(2));

        Assert.NotNull(cut.Find(".pipeline-catalog-panel .pipeline-catalog-filters"));
        Assert.Empty(cut.FindComponents<PipelineRunsGrid>());
        // R-215: no tab strip any more, the two views are one select bar in the page header.
        Assert.Empty(cut.FindAll("[role='tab']"));
        Assert.Equal(2, ViewItems().Count);
        Assert.True(cut.FindComponent<PipelineDependencyGrid>().Instance.FillHeight);

        cut.Find(".pipeline-list-search").Input("Build");

        cut.WaitForAssertion(() =>
        {
            var grid = cut.FindComponent<PipelineDependencyGrid>().Instance;
            Assert.Single(grid.Items);
            Assert.Equal("Build", grid.Items[0].Name);
        });
    }

    [Fact]
    public void GlobalScope_RendersParentAndResolvedReference()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(parents:
        [
            new PipelineDependencyDto
            {
                Id = 9,
                Name = "release",
                ProjectName = "Aetheus",
                TriggerType = PipelineTriggerType.Manual,
                References = [new PipelineDependencyReferenceDto(10, "deploy")]
            }
        ], leaves:
        [
            new PipelineDependencyDto
            {
                Id = 10,
                Name = "deploy",
                ProjectName = "Aetheus",
                Parents = [new PipelineDependencyReferenceDto(9, "release")]
            }
        ]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>
        {
            new() { Id = 12, PipelineId = 10, PipelineName = "deploy", ProjectName = "Aetheus", Status = PipelineStatus.Success }
        });

        var cut = RenderList();
        cut.WaitForState(() => cut.Markup.Contains("release"), TimeSpan.FromSeconds(2));

        // R-121 / R-215: parent and leaf share the one catalogue grid; the header views are Catalogue (n) and Runs (n).
        var tabs = ViewItems();
        Assert.Equal(2, tabs.Count);
        Assert.Contains("PipelineCatalogTab (2)", tabs[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Runs (1)", tabs[1].TextContent, StringComparison.Ordinal);
        var catalogue = Assert.Single(cut.FindComponents<PipelineDependencyGrid>()).Instance;
        Assert.Equal(["deploy", "release"], catalogue.Items.Select(item => item.Name));
        Assert.True(catalogue.Virtualize);
        Assert.Contains("MoreActions", cut.Markup);
        Assert.Contains("Run", cut.Markup);

        OpenRunsTab(cut);
        cut.WaitForAssertion(() => Assert.Contains(">#12<", cut.Markup));
        var runsGrid = cut.FindComponent<PipelineRunsGrid>().Instance;
        Assert.True(runsGrid.Virtualize);
        Assert.True(runsGrid.FillHeight);
        Assert.Contains("pipeline-grid-fill", cut.Markup);
        Assert.DoesNotContain("aria-label=\"Pagination\"", cut.Markup);
    }

    [Fact]
    public void ServerScope_RendersTheSameOverviewStructureAsProjectScope()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList(serverId: 42);
        cut.WaitForState(() => _handler.Requests.Any(request => request.Url.Contains("runs/recent")), TimeSpan.FromSeconds(2));

        Assert.Contains("PipelineCatalogTab (0)", _host!.Markup);
        Assert.Contains("Runs (0)", _host!.Markup);
        Assert.DoesNotContain("progressbar", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunningAPipeline_GoesStraightToTheNewRun_WithoutReloadingTheOverview()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
            [new PipelineDependencyDto { Id = 11, Name = "Current", TriggerType = PipelineTriggerType.Manual }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/11/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/11/parameters", new List<PipelineRunParameterDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/11/run", new PipelineRunDto { Id = 2470, PipelineId = 11 });
        var cut = RenderList();
        cut.WaitForState(() => cut.Markup.Contains("Current"), TimeSpan.FromSeconds(2));
        var overviewLoads = _handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies"));

        var method = typeof(PipelinesList).GetMethod("RunPipelineClick", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [11, null])!);

        Assert.EndsWith("/pipelines/runs/2470",
            Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Equal(overviewLoads, _handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies")));
    }

    [Fact]
    public async Task HubEvent_ServerScope_RefreshesForNewPipelineId()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
            [new PipelineDependencyDto { Id = 11, Name = "Current", TriggerType = PipelineTriggerType.Manual }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList(serverId: 42);
        cut.WaitForState(() => cut.Markup.Contains("Current"), TimeSpan.FromSeconds(2));
        var before = _handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies"));
        var method = typeof(PipelinesList).GetMethod("OnPipelineHubEvent", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [999])!);
        cut.WaitForAssertion(() => Assert.True(
            _handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies")) > before),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task HubEvent_ProjectScope_RefreshesForNewPipelineId()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList(projectId: 7);
        cut.WaitForState(() => _handler.Requests.Any(request => request.Url.Contains("api/pipelines/dependencies")), TimeSpan.FromSeconds(2));
        var before = _handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies"));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>
        {
            new() { Id = 123, PipelineId = 999, PipelineName = "Live pipeline", Status = PipelineStatus.Running }
        });
        var method = typeof(PipelinesList).GetMethod("OnPipelineHubEvent", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [999])!);

        cut.WaitForAssertion(() => Assert.True(
            _handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies")) > before),
            TimeSpan.FromSeconds(3));
        // The run id cell now prefixes the id with "#" since it shares its column with the row actions.
        OpenRunsTab(cut);
        cut.WaitForAssertion(() => Assert.Contains(">#123<", cut.Markup), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task PipelineProgressEvent_RefreshesGlobalRecentRuns()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList();
        cut.WaitForState(() => _handler.Requests.Any(request => request.Url.Contains("runs/recent")), TimeSpan.FromSeconds(2));
        var before = _handler.Requests.Count(request => request.Url.Contains("runs/recent"));
        var method = typeof(PipelinesList).GetMethod("OnPipelineProgressEvent", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        cut.WaitForAssertion(() => Assert.True(
            _handler.Requests.Count(request => request.Url.Contains("runs/recent")) > before),
            TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// R-10: pressing Run, and every run event after it, reloaded the page behind the page-wide
    /// loader, so "Loading" replaced the runs the launch had just added. A reload keeps the page.
    /// </summary>
    [Fact]
    public async Task AReloadAfterTheFirstLoad_KeepsThePageInsteadOfThePageWideLoader()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
            [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.Contains("Build", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(2));

        var pending = new TaskCompletionSource<PipelineDependencyGroupsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/dependencies", _ => pending.Task);
        var method = typeof(PipelinesList).GetMethod("OnPipelineProgressEvent", Priv)!;
        var reload = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        // Polled without blocking: the reload runs on the renderer, which a blocking wait would starve.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies")) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(2, _handler.Requests.Count(request => request.Url.Contains("api/pipelines/dependencies")));
        // Any render while the reload is in flight - a run event, a toast - showed the loader.
        cut.Render();
        Assert.Empty(cut.FindAll(".aetheus-loader.omni-logo-loader--large"));
        Assert.Contains("Build", cut.Markup, StringComparison.Ordinal);

        pending.SetResult(Groups(leaves: [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
        await reload;
    }

    [Fact]
    public async Task PipelineProgressEvent_RefreshesProjectRecentRuns()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList(projectId: 7);
        cut.WaitForState(() => _handler.Requests.Any(request => request.Url.Contains("runs/recent?projectId=7")), TimeSpan.FromSeconds(2));
        var before = _handler.Requests.Count(request => request.Url.Contains("runs/recent?projectId=7"));
        var method = typeof(PipelinesList).GetMethod("OnPipelineProgressEvent", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        cut.WaitForAssertion(() => Assert.True(
            _handler.Requests.Count(request => request.Url.Contains("runs/recent?projectId=7")) > before),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void RecentRuns_AreDefensivelyLimitedToTwenty()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups());
        _handler.SetJsonResponse("api/pipelines/runs/recent", Enumerable.Range(1, 25)
            .Select(id => new PipelineRunDto
            {
                Id = id,
                PipelineId = 1,
                PipelineName = "CI",
                Status = PipelineStatus.Success,
                StartedAt = new DateTime(2026, 1, 1).AddMinutes(id)
            })
            .ToList());

        var cut = RenderList();
        OpenRunsTab(cut);
        cut.WaitForState(() => cut.FindComponents<PipelineRunsGrid>().Count == 1
            && cut.FindComponent<PipelineRunsGrid>().Instance.Items.Count == 20,
            TimeSpan.FromSeconds(2));

        Assert.Equal(20, cut.FindComponent<PipelineRunsGrid>().Instance.Items.Count);
    }

    [Fact]
    public async Task ClearFilters_ResetsSearchAndTrigger()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList();
        typeof(PipelinesList).GetField("_search", Priv)!.SetValue(cut.Instance, "build");
        typeof(PipelinesList).GetField("_triggerFilters", Priv)!.SetValue(cut.Instance, (IReadOnlyList<PipelineTriggerType>)[PipelineTriggerType.Schedule]);
        typeof(PipelinesList).GetField("_favoritesOnly", Priv)!.SetValue(cut.Instance, true);

        await cut.InvokeAsync(() => (Task)typeof(PipelinesList).GetMethod("ClearFilters", Priv)!.Invoke(cut.Instance, [])!);

        Assert.Null(typeof(PipelinesList).GetField("_search", Priv)!.GetValue(cut.Instance));
        Assert.Empty((IReadOnlyList<PipelineTriggerType>)typeof(PipelinesList).GetField("_triggerFilters", Priv)!.GetValue(cut.Instance)!);
        Assert.False((bool)typeof(PipelinesList).GetField("_favoritesOnly", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task NewPipeline_NavigatesToCreatePage()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList();

        await cut.InvokeAsync(() => typeof(PipelinesList).GetMethod("NewPipeline", Priv)!.Invoke(cut.Instance, []));

        Assert.Contains("pipelines/setup", Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri);
    }

    /// <summary>R-120 / R-215: the runs grid is the second view, picked in the page header, and
    /// renders only once it is open.</summary>
    private void OpenRunsTab(IRenderedComponent<PipelinesList> cut)
    {
        cut.WaitForState(() => ViewItems().Count == 2, TimeSpan.FromSeconds(2));
        ViewItems()[1].Click();
        cut.WaitForState(() => cut.FindComponents<PipelineRunsGrid>().Count == 1, TimeSpan.FromSeconds(2));
    }

    /// <summary>R-215: the view switch sits in the page header (the host's outlet), not in the list.</summary>
    private IReadOnlyList<AngleSharp.Dom.IElement> ViewItems() =>
        _host!.FindAll(".pipeline-view-switch .omni-select-bar__item");

    [Fact]
    public async Task R2100_LeavingThePageWhileItLoads_DoesNotUseTheDisposedLiveLifetime()
    {
        // Recette R2-100: "ObjectDisposedException - CancellationTokenSource_Disposed" reached the error
        // boundary. Leaving a pipelines page before its overview had loaded disposed the live lifetime,
        // then the resumed initialisation read its Token to start the live recovery loop.
        var overview = new TaskCompletionSource<PipelineDependencyGroupsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/dependencies", _ => overview.Task);
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var cut = RenderList();

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        overview.SetResult(Groups(leaves: [new PipelineDependencyDto { Id = 7, Name = "Build" }]));

        var unhandled = Renderer.UnhandledException;
        var first = await Task.WhenAny(unhandled, Task.Delay(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken));
        Assert.True(first != unhandled, $"Unhandled after the page was left: {(first == unhandled ? await unhandled : null)}");
    }

    /// <summary>
    /// Recette R2-087: back on the list, the catalogue and its search are there at once, without the
    /// page-wide loader, while the overview is read again behind them.
    /// </summary>
    [Fact]
    public async Task R2087_ComingBack_ShowsTheListAndItsSearchAtOnce()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", Groups(leaves:
            [new PipelineDependencyDto { Id = 7, Name = "Build" }, new PipelineDependencyDto { Id = 8, Name = "Deploy" }]));
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        var first = RenderList();
        first.WaitForState(() => first.FindComponents<PipelineDependencyGrid>().Count == 1, TimeSpan.FromSeconds(2));
        typeof(PipelinesList).GetField("_search", Priv)!.SetValue(first.Instance, "build");
        // Leaving the page disposes it, which keeps what it showed.
        await DisposeComponentsAsync();

        var overview = new TaskCompletionSource<PipelineDependencyGroupsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/dependencies", _ => overview.Task);
        var back = RenderList();

        Assert.Empty(back.FindAll(".aetheus-loader"));
        var grid = Assert.Single(back.FindComponents<PipelineDependencyGrid>()).Instance;
        Assert.Equal(["Build"], grid.Items.Select(item => item.Name));
        Assert.Equal("build", typeof(PipelinesList).GetField("_search", Priv)!.GetValue(back.Instance));
        overview.SetResult(Groups(leaves: [new PipelineDependencyDto { Id = 7, Name = "Build" }]));
    }

    /// <summary>The list is rendered next to the header outlet its hosts give it, as a real page does.</summary>
    private IRenderedComponent<PipelinesList> RenderList(int? projectId = null, int? serverId = null, int? templateId = null)
    {
        _host = Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PipelinesList.HeaderActionsSection);
            builder.CloseComponent();
            builder.OpenComponent<PipelinesList>(2);
            builder.AddAttribute(3, nameof(PipelinesList.ProjectId), projectId);
            builder.AddAttribute(4, nameof(PipelinesList.ServerId), serverId);
            builder.AddAttribute(5, nameof(PipelinesList.TemplateId), templateId);
            builder.CloseComponent();
        });
        return _host.FindComponent<PipelinesList>();
    }

    private static PaginatedResult<PipelineDependencyDto> Page(params PipelineDependencyDto[] items) => new()
    {
        Items = items.ToList(),
        TotalCount = items.Length,
        Page = 1,
        PageSize = 25
    };

    private static PipelineDependencyGroupsDto Groups(
        List<PipelineDependencyDto>? parents = null,
        List<PipelineDependencyDto>? leaves = null) => new()
        {
            Parents = parents ?? [],
            Leaves = leaves ?? []
        };
}
