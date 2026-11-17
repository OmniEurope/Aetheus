// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary> Recette R-51 (ADR-058): what a project "deleted, to clean up" shows. The panel carries the warning and
/// the single relaunch action; the "Suppression à terminer" badge exists only for a project in deletion, in the
/// tiles, the dense rows and the project header. (The badge on a pending tile or row is already covered by
/// ProjectsTests, and on the header by ProjectDetailLayoutTests.) </summary>
public sealed class ProjectDeletionPendingPanelTests : BunitContext
{
    private const string Badge = "[data-testid='project-deletion-pending']";
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDeletionPendingPanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private static PaginatedResult<ProjectDto> Projects(DateTime? deletionRequestedAt) => new()
    {
        Items = [new ProjectDto { Id = 9, Name = "Subject", Status = ProjectStatus.Active, DeletionRequestedAt = deletionRequestedAt }],
        TotalCount = 1
    };

    private async Task<IRenderedComponent<ProjectDetailLayout>> RenderLayoutAsync(DateTime? deletionRequestedAt)
    {
        _handler.SetJsonResponse("api/projects/42", new ProjectDetailDto
        {
            Id = 42,
            Name = "Atlas",
            Status = ProjectStatus.Active,
            DeletionRequestedAt = deletionRequestedAt
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects/42/overview");
        var cut = Render<ProjectDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<div/>")));
        await cut.InvokeAsync(() => loader.EnsureLoadedAsync(42));
        return cut;
    }

    [Fact]
    public void Panel_ShowsTheWarningTitleTheIntroAndOneEnabledRelaunchButton()
    {
        var cut = Render<ProjectDeletionPendingPanel>(parameters => parameters.Add(panel => panel.ProjectId, 7));

        var alert = cut.Find(".omni-alert").TextContent;
        Assert.Contains("ProjectDeletionPending", alert, StringComparison.Ordinal);
        Assert.Contains("ProjectDeletionResumeIntro", alert, StringComparison.Ordinal);
        var relaunch = Assert.Single(cut.FindAll("button"));
        Assert.Equal("project-deletion-relaunch", relaunch.Id);
        Assert.False(relaunch.HasAttribute("disabled"));
        Assert.Contains("ProjectDeletionRelaunchDeletion", relaunch.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_Relaunch_OpensTheDeletionDialogOnceEachClick()
    {
        var cut = Render<ProjectDeletionPendingPanel>(parameters => parameters.Add(panel => panel.ProjectId, 7));
        var spy = (SpyDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.Equal(0, spy.OpenCount);

        cut.Find("#project-deletion-relaunch").Click();

        Assert.Equal(1, spy.OpenCount);
        Assert.Equal(nameof(ProjectDeleteDialog), spy.LastOpenedComponent);
    }

    [Fact]
    public void Tile_OfANormalProject_HasNoDeletionBadge()
    {
        _handler.SetJsonResponse("api/projects", Projects(null));
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Subject"));

        Assert.NotEmpty(cut.FindAll(".project-card"));
        Assert.Empty(cut.FindAll(Badge));
    }

    [Fact]
    public void DenseRow_OfANormalProject_HasNoDeletionBadge()
    {
        _handler.SetJsonResponse("api/projects", Projects(null));
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Subject"));
        cut.Find("button[title='DenseView']").Click();

        Assert.NotEmpty(cut.FindAll(".project-dense-row"));
        Assert.Empty(cut.FindAll(Badge));
    }

    [Fact]
    public async Task Layout_OfAProjectInDeletion_ShowsTheBadgeAndThePanel()
    {
        var cut = await RenderLayoutAsync(new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(Badge));
            Assert.NotEmpty(cut.FindAll("[data-testid='project-deletion-pending-panel']"));
        });
    }

    [Fact]
    public async Task Layout_OfANormalProject_ShowsNeitherTheBadgeNorThePanel()
    {
        var cut = await RenderLayoutAsync(null);

        cut.WaitForAssertion(() => Assert.NotNull(Services.GetRequiredService<ProjectDetailLoader>().Project));
        Assert.Empty(cut.FindAll(Badge));
        Assert.Empty(cut.FindAll("[data-testid='project-deletion-pending-panel']"));
    }
}
