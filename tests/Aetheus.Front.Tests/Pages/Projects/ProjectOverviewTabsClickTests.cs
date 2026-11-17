// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Recette R-41: on the project overview opened by a link naming a tab (?tab=expand-contract), a click on
/// each of the tabs must select it and put it in the address. The navigation manager here answers like the
/// browser one: NavigateTo only records the target, the location change (and so the new ?tab value the
/// tabs read) arrives later, and the page can render in between.
/// </summary>
public class ProjectOverviewTabsClickTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly DeferredNavigationManager _nav = new();

    public ProjectOverviewTabsClickTests()
    {
        Services.AddSingleton<NavigationManager>(_nav); _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ProjectOverviewSection> OpenOverview(string query, ProjectDetailDto project)
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/environments?", Array.Empty<EnvironmentDto>());
        _nav.Start($"http://localhost/projects/1/overview{query}");
        return Render<ProjectOverviewSection>(p => p.Add(x => x.Project, project));
    }

    private static ProjectDetailDto ProjectWithExpandContract() =>
        new() { Id = 1, Name = "Web", MigrationGateEnabled = true };

    private static string? SelectedTab(IRenderedComponent<ProjectOverviewSection> cut) =>
        cut.FindAll("[role='tab']").SingleOrDefault(tab => tab.GetAttribute("aria-selected") == "true")?.GetAttribute("data-key");

    [Theory]
    [InlineData("?tab=expand-contract")]
    [InlineData("")]
    public void ClickingEachTab_SelectsIt_AndWritesItInTheAddress(string query)
    {
        var cut = OpenOverview(query, ProjectWithExpandContract());

        foreach (var (key, expectedQuery) in new[]
                 {
                     ("readme", "?tab=readme"),
                     ("changelog", "?tab=changelog"),
                     ("expand-contract", "?tab=expand-contract"),
                     ("overview", ""),
                     ("readme", "?tab=readme"),
                 })
        {
            cut.Find($"[role='tab'][data-key='{key}']").Click();
            Assert.Equal($"http://localhost/projects/1/overview{expectedQuery}", _nav.Target);
            Assert.Equal(key, SelectedTab(cut));

            cut.InvokeAsync(_nav.Arrive);
            cut.WaitForAssertion(() => Assert.Equal(key, SelectedTab(cut)));
            Assert.Equal($"http://localhost/projects/1/overview{expectedQuery}", _nav.Uri);
        }
    }

    /// <summary>The page renders between the click and the arrival of the new address (a hub push, a toast, the
    /// permissions): the clicked tab stays selected instead of snapping back to the first one.</summary>
    [Fact]
    public void APageRender_BetweenTheClickAndTheNewAddress_KeepsTheClickedTab()
    {
        var project = ProjectWithExpandContract();
        var cut = OpenOverview("?tab=expand-contract", project);
        Assert.Equal("expand-contract", SelectedTab(cut));

        cut.Find("[role='tab'][data-key='readme']").Click();
        Assert.Equal("readme", SelectedTab(cut));
        cut.Render(p => p.Add(x => x.Project, project));
        Assert.Equal("readme", SelectedTab(cut));

        cut.InvokeAsync(_nav.Arrive);
        cut.Render(p => p.Add(x => x.Project, project));
        Assert.Equal("readme", SelectedTab(cut));
    }

    private sealed class DeferredNavigationManager : NavigationManager
    {
        public DeferredNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        public string? Target { get; private set; }

        public void Start(string uri) => Uri = uri;

        public void Arrive()
        {
            Uri = Target!;
            NotifyLocationChanged(false);
        }

        protected override void NavigateToCore(string uri, NavigationOptions options) => Target = ToAbsoluteUri(uri).ToString();
    }
}
