// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// Recette R2-122: the project deletion confirmation says what goes with the project, read from the
/// server's preview, and wakes the Delete button only for the exact project name, never while a run
/// of the project is in progress.
/// </summary>
public sealed class ProjectDeleteDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDeleteDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ProjectDeletionPreviewDto Preview(int activeRuns = 0) => new()
    {
        ProjectId = 7,
        ProjectName = "Portfolio",
        ActiveRunCount = activeRuns,
        Items =
        [
            new ProjectDeletionItemDto(ProjectDeletionItemKind.Pipelines, 4),
            new ProjectDeletionItemDto(ProjectDeletionItemKind.TaskLogLines, 1234),
            new ProjectDeletionItemDto(ProjectDeletionItemKind.MonitoredApps, 1),
        ],
    };

    private IRenderedComponent<ProjectDeleteDialog> RenderDialog() =>
        Render<ProjectDeleteDialog>(parameters => parameters.Add(dialog => dialog.ProjectId, 7));

    private static bool DeleteEnabled(IRenderedComponent<ProjectDeleteDialog> cut) =>
        !cut.Find("#project-delete-submit").HasAttribute("disabled");

    [Fact]
    public void Shows_WhatTheDeletionRemoves_CountedPerKind()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());

        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.Contains("Enum_ProjectDeletionItemKind_TaskLogLines", cut.Find(".project-deletion-counts").TextContent));
        var counts = cut.Find(".project-deletion-counts").TextContent;
        // The test localizer returns the key: each kind is labelled through its enum key.
        Assert.Contains("Enum_ProjectDeletionItemKind_Pipelines", counts);
        Assert.Contains("4", counts);
        Assert.Contains("1234", counts);
        Assert.Contains("Enum_ProjectDeletionItemKind_MonitoredApps", counts);
        Assert.DoesNotContain("Enum_ProjectDeletionItemKind_Vaults", counts);
        Assert.Contains("ProjectDeletionTypeName", cut.Markup);
    }

    [Fact]
    public void Delete_StaysDisabled_UntilTheExactProjectNameIsTyped_ThenDeletesAndCloses()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find("#project-delete-confirm"));

        Assert.False(DeleteEnabled(cut));
        cut.Find("#project-delete-confirm").Input("portfolio");
        Assert.False(DeleteEnabled(cut));
        cut.Find("#project-delete-confirm").Input("Portfolio");
        Assert.True(DeleteEnabled(cut));

        cut.Find("#project-delete-submit").Click();

        cut.WaitForAssertion(() => Assert.Equal(true, Spy().LastResult));
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success && toast.Detail == "Deleted");
        Assert.Contains(_handler.Requests, request => request.Method == "DELETE" && request.Url.EndsWith("api/projects/7", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunInProgress_IsSaid_AndKeepsDeleteDisabled_EvenWithTheName()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview(activeRuns: 2));
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find(".project-deletion-active-runs"));

        cut.Find("#project-delete-confirm").Input("Portfolio");

        Assert.Contains("ProjectDeletionActiveRuns", cut.Find(".project-deletion-active-runs").TextContent);
        Assert.False(DeleteEnabled(cut));
    }

    [Fact]
    public void APreviewThatCannotBeRead_SaysSo_AndOffersNoDeletion()
    {
        _handler.SetResponse("api/projects/7/deletion-preview", HttpStatusCode.Forbidden);

        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.Contains("ProjectDeletionPreviewFailed", cut.Markup));
        Assert.Empty(cut.FindAll("#project-delete-confirm"));
        Assert.False(DeleteEnabled(cut));
    }
}
