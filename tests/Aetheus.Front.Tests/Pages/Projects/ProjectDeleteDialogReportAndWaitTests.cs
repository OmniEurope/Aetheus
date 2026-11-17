// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary> Recette R-6: at the end of a deletion, "Supprimé" is folded by default with its count and
/// "Non supprimé" is a compact table whose reason wraps. Recette R-29: the button that relaunches a deletion
/// is red and says "Relancer la suppression", and while the dialog works out what will be deleted it says so,
/// in sight, with the confirm button disabled. </summary>
public sealed class ProjectDeleteDialogReportAndWaitTests : BunitContext
{
    private const string Preview = "api/projects/7/deletion-preview";
    private const string Discovery = "api/projects/7/deletion/discovery";
    private const string Cleanup = "api/projects/7/deletion/cleanup";
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDeleteDialogReportAndWaitTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private static ProjectDeletionPreviewDto PreviewDto() => new()
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

    [Fact]
    public void R6_TheEnd_FoldsTheDeletedByDefaultWithItsCount_AndShowsTheKeptAsACompactTableWithAWrappingReason()
    {
        _handler.SetJsonResponse(Preview, PreviewDto());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto>
        {
            Task(41, ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded, Container, Certificate)
        });
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Failed,
                Container with { Outcome = ProjectServerResourceOutcome.Removed },
                Certificate with { Outcome = ProjectServerResourceOutcome.Failed, Reason = "an enabled site still uses it" })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.Contains("portfolio-prod-front-1", cut.Markup));
        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-submit").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<OmniDataGrid<ProjectDeletionReportRow>>().Count), TimeSpan.FromSeconds(30));

        var deleted = cut.Find(".project-deletion-report-deleted");
        var details = deleted.TagName.Equals("details", StringComparison.OrdinalIgnoreCase) ? deleted : deleted.QuerySelector("details") ?? deleted;
        Assert.False(details.HasAttribute("open") || deleted.HasAttribute("open"), "The deleted list must be folded by default.");
        var legend = deleted.QuerySelector("summary, legend");
        Assert.NotNull(legend);
        Assert.Contains("ProjectDeletionReportDeleted", legend.TextContent);
        Assert.Contains("(1)", legend.TextContent);

        var kept = cut.FindComponents<OmniDataGrid<ProjectDeletionReportRow>>()[1].Instance;
        Assert.False(kept.FillAvailableHeight, "The kept table must be sized to its content.");
        // Below the cap the table is as tall as its rows; the cap only bounds it, as every table must be (R-327).
        Assert.Equal(AetheusGrid.ScrollMaxHeight, kept.MaxHeight);
        var reason = cut.FindComponents<OmniDataGridColumn<ProjectDeletionReportRow>>()
            .Single(column => column.Instance.Property == "Detail" && column.Instance.Title == "ProjectDeletionReportReason");
        Assert.True(reason.Instance.Wrap, "The reason column must wrap on several lines.");
    }

    [Fact]
    public void R29_ThePanelOfAProjectWaitingForItsCleanup_HasADangerButtonLabelledRelaunchTheDeletion()
    {
        var cut = Render<ProjectDeletionPendingPanel>(parameters => parameters.Add(panel => panel.ProjectId, 7));

        var button = cut.Find("#project-deletion-relaunch");
        Assert.Contains("danger", button.ClassName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProjectDeletionRelaunchDeletion", button.TextContent);
    }

    [Fact]
    public void R29_WhilePreviewIsLoading_TheDialogSaysItIsCalculating_AndWhileServersAreRead_TheConfirmButtonStaysDisabled()
    {
        var previewGate = new TaskCompletionSource<ProjectDeletionPreviewDto>();
        var discoveryGate = new TaskCompletionSource<List<ProjectServerResourceTaskDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, Preview, _ => previewGate.Task);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, Discovery, _ => discoveryGate.Task);
        var cut = RenderDialog();

        Assert.Contains("ProjectDeletionCalculating", cut.Find(".project-deletion-calculating").TextContent);
        Assert.NotEmpty(cut.FindAll(".project-deletion-calculating .omni-page-veil"));

        previewGate.SetResult(PreviewDto() with { DeletionRequestedAt = DateTime.UtcNow });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#project-delete-submit")));
        // The preview is in, the servers are being read: the message stays in sight (not only in the folded
        // "Afficher plus") and the confirm button does not wake up.
        Assert.Contains("ProjectDeletionCalculating", cut.Find(".project-deletion-calculating").TextContent);
        Assert.True(cut.Find("#project-delete-submit").HasAttribute("disabled"));

        discoveryGate.SetResult([Task(41, ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded, Container)]);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".project-deletion-calculating")));
        Assert.False(cut.Find("#project-delete-submit").HasAttribute("disabled"));
    }
}
