// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary> Recette R-50 and R-51 (ADR-058): the deletion runs under one veil, set once and cleared once, with
/// nothing moving under it while the agents work; it ends in the same dialog on what was deleted and what was not
/// (with the reason), each as a table, with a single Fermer that goes back to the projects list. When something
/// is left on a server, the project is not half deleted: it waits, "Suppression à terminer", for the cleanup to be
/// relaunched. </summary>
public sealed class ProjectDeleteDialogOutcomeTests : BunitContext
{
    private const string Discovery = "api/projects/7/deletion/discovery";
    private const string Cleanup = "api/projects/7/deletion/cleanup";
    private const string Tasks = "api/projects/7/deletion/tasks";
    private const string Zone = "[data-testid='project-delete-zone']";
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDeleteDialogOutcomeTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ProjectDeletionPreviewDto Preview() => new()
    {
        ProjectId = 7,
        ProjectName = "Portfolio",
        Items = [new ProjectDeletionItemDto(ProjectDeletionItemKind.Pipelines, 4)],
        Servers =
        [
            new ProjectDeletionServerDto
            {
                ServerId = 3,
                ServerName = "vps-prod",
                State = ProjectDeletionServerState.Ready,
                Environments = [new ProjectDeletionEnvironmentDto { Name = "prod", ComposeProject = "portfolio-prod" }],
            }
        ],
    };

    private static readonly ProjectServerResourceDto Container =
        new(ProjectServerResourceKind.Container, "portfolio-prod-front-1", "portfolio-prod", "prod", ProjectServerResourceOutcome.Found, null);
    private static readonly ProjectServerResourceDto Certificate =
        new(ProjectServerResourceKind.Certificate, "app.portfolio.example", "app.portfolio.example", "prod", ProjectServerResourceOutcome.Found, null);

    private static ProjectServerResourceTaskDto Task(int id, ProjectServerTaskPhase phase, ProjectServerTaskState state,
        params ProjectServerResourceDto[] resources) => new()
        {
            TaskId = id,
            ServerId = 3,
            ServerName = "vps-prod",
            Phase = phase,
            State = state,
            Resources = [.. resources]
        };

    private IRenderedComponent<ProjectDeleteDialog> RenderDialog() =>
        Render<ProjectDeleteDialog>(parameters => parameters.Add(dialog => dialog.ProjectId, 7));

    private void ConfirmAndDelete(IRenderedComponent<ProjectDeleteDialog> cut)
    {
        cut.WaitForAssertion(() => Assert.Contains("portfolio-prod-front-1", cut.Markup));
        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-submit").Click();
    }

    [Fact]
    public void R50_TheVeil_IsSetOnceAndClearedOnce_WithNoLoaderMovingUnderIt_WhileTheAgentsWork()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto>
        {
            Task(41, ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded, Container)
        });
        // The removal is still running when it is queued: the dialog follows it until the agent answers.
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Running)
        });
        _handler.SetJsonResponse(Tasks, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Succeeded, Container with { Outcome = ProjectServerResourceOutcome.Removed })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();
        var veiled = new List<bool>();
        var loaderUnderTheVeil = false;
        cut.OnMarkupUpdated += (_, _) =>
        {
            var zones = cut.FindAll(Zone);
            if (zones.Count == 0) return;
            var busy = zones[0].ClassList.Contains("omni-busy");
            if (veiled.Count == 0 || veiled[^1] != busy) veiled.Add(busy);
            if (busy && zones[0].QuerySelector(".aetheus-loader") is not null) loaderUnderTheVeil = true;
        };

        ConfirmAndDelete(cut);

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Method == "DELETE"), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.False(cut.Find(Zone).ClassList.Contains("omni-busy")), TimeSpan.FromSeconds(10));
        Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.Contains(Tasks, StringComparison.Ordinal));
        Assert.Equal([false, true, false], veiled);
        Assert.False(loaderUnderTheVeil, "A loader appeared under the veil while the agents worked.");
    }

    [Fact]
    public void R50_TheEnd_ListsWhatWasDeletedAndWhatWasNot_InTwoTables_NoBadge_AndASingleFermerGoesBackToTheProjects()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto>
        {
            Task(41, ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded, Container, Certificate)
        });
        // R-51 guard kept by the agent: a certificate an enabled site may still use is not removed.
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Failed,
                Container with { Outcome = ProjectServerResourceOutcome.Removed },
                Certificate with { Outcome = ProjectServerResourceOutcome.Failed, Reason = "an enabled site still uses it" })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();
        ConfirmAndDelete(cut);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<OmniDataGrid<ProjectDeletionReportRow>>().Count), TimeSpan.FromSeconds(5));
        var grids = cut.FindComponents<OmniDataGrid<ProjectDeletionReportRow>>();
        Assert.Equal([new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_Container portfolio-prod-front-1", "vps-prod", "prod")], grids[0].Instance.Items);
        Assert.Equal(
            [
                new ProjectDeletionReportRow("ProjectDeletionAetheusData", "ProjectDeletionPlaceAetheus", "ProjectDeletionAetheusDataKept"),
                new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_Certificate app.portfolio.example", "vps-prod", "an enabled site still uses it"),
            ],
            grids[1].Instance.Items);
        Assert.Contains("ProjectDeletionReportDeleted", cut.Find(".project-deletion-report-deleted").TextContent);
        Assert.Contains("ProjectDeletionReportNotDeleted", cut.Find(".project-deletion-report-kept").TextContent);
        Assert.Empty(cut.Find(".project-deletion-report-deleted").QuerySelectorAll(".omni-badge"));
        Assert.Empty(cut.Find(".project-deletion-report-kept").QuerySelectorAll(".omni-badge"));

        // One way out, neutral: Fermer, back to the projects list.
        var buttons = cut.Find(".dialog-footer").QuerySelectorAll("button");
        var close = Assert.Single(buttons);
        Assert.Contains("Close", close.TextContent);
        Assert.Contains("secondary", close.ClassName, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("#project-delete-submit"));
        close.Click();

        Assert.Equal(true, Spy().LastResult);
        Assert.EndsWith("/projects", Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void R50_AFullDeletion_ListsTheAetheusDataAmongWhatWasDeleted()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto>
        {
            Task(41, ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded, Container)
        });
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Succeeded, Container with { Outcome = ProjectServerResourceOutcome.Removed })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();
        ConfirmAndDelete(cut);

        cut.WaitForAssertion(() => Assert.Contains("ProjectDeletionDone", cut.Find(".project-deletion-outcome").TextContent), TimeSpan.FromSeconds(5));
        var grids = cut.FindComponents<OmniDataGrid<ProjectDeletionReportRow>>();
        Assert.Contains(new ProjectDeletionReportRow("Enum_ProjectDeletionItemKind_Pipelines", "ProjectDeletionPlaceAetheus", "4"), grids[0].Instance.Items!);
        Assert.Empty(grids[1].Instance.Items!);
    }

    [Fact]
    public void R51_AProjectWaitingForItsCleanup_RelaunchesIt_WithoutAskingItsNameAgain()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview() with { DeletionRequestedAt = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc) });
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto>
        {
            Task(51, ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded, Certificate)
        });
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(52, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Succeeded, Certificate with { Outcome = ProjectServerResourceOutcome.Removed })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.Contains("app.portfolio.example", cut.Markup));

        Assert.Empty(cut.FindAll("#project-delete-confirm"));
        Assert.Contains("ProjectDeletionRelaunch", cut.Find("#project-delete-submit").TextContent);
        Assert.Contains("ProjectDeletionResumeIntro", cut.Markup);
        cut.Find("#project-delete-submit").Click();

        cut.WaitForAssertion(() => Assert.Contains("ProjectDeletionDone", cut.Find(".project-deletion-outcome").TextContent), TimeSpan.FromSeconds(5));
        var delete = _handler.RequestsSnapshot().Single(request => request.Method == "DELETE");
        Assert.Contains("serverTaskIds=51", delete.Url, StringComparison.Ordinal);
        Assert.Contains("serverTaskIds=52", delete.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void R51_ThePanelOfAProjectWaitingForItsCleanup_OpensTheDeletionToRelaunchIt()
    {
        var cut = Render<ProjectDeletionPendingPanel>(parameters => parameters.Add(panel => panel.ProjectId, 7));

        Assert.Contains("ProjectDeletionPending", cut.Markup);
        cut.Find("#project-deletion-relaunch").Click();

        Assert.Equal(1, Spy().OpenCount);
        Assert.Equal(nameof(ProjectDeleteDialog), Spy().LastOpenedComponent);
    }
}
