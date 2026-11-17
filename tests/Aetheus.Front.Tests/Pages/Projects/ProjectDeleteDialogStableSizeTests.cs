// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary> Recette R-71: the deletion dialog keeps about the same size from the first phase to the report. The
/// dialog opens with one fixed height, and in every phase the content scrolls in its own area while the button
/// footer stays outside it, so nothing moves when a phase appears or disappears. </summary>
public sealed class ProjectDeleteDialogStableSizeTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDeleteDialogStableSizeTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private static ProjectDeletionPreviewDto Preview() => new()
    {
        ProjectId = 7,
        ProjectName = "Portfolio",
        Items = [new ProjectDeletionItemDto(ProjectDeletionItemKind.Pipelines, 4)],
    };

    private IRenderedComponent<ProjectDeleteDialog> RenderDialog() =>
        Render<ProjectDeleteDialog>(parameters => parameters.Add(dialog => dialog.ProjectId, 7));

    /// <summary>The same frame in every phase: a sized root, a scrolling body, and the footer outside that body.</summary>
    private static void AssertStableFrame(IRenderedComponent<ProjectDeleteDialog> cut)
    {
        var root = cut.Find("[data-testid='project-delete-zone']");
        Assert.Contains("project-delete-dialog", root.ClassList);
        var body = Assert.Single(root.QuerySelectorAll(":scope > .project-delete-dialog__body"));
        var footer = Assert.Single(root.QuerySelectorAll(":scope > .dialog-footer"));
        Assert.Empty(body.QuerySelectorAll(".dialog-footer"));
        Assert.NotSame(body, footer);
    }

    [Fact]
    public async Task TheDialog_OpensWithOneFixedHeight()
    {
        OmniDialogOptions? seen = null;
        var spy = (SpyDialogService)Services.GetRequiredService<OmniDialogService>();
        spy.OnOpen += (_, _, _, options) => seen = options;
        var localizer = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>();

        _ = ProjectDeleteDialog.OpenAsync(spy, localizer, 7);
        await Task.Yield();

        Assert.NotNull(seen);
        Assert.False(string.IsNullOrWhiteSpace(seen!.Height));
        Assert.Equal("640px", seen.Width);
    }

    [Fact]
    public void FirstRender_HasTheStableFrame()
    {
        // Whatever phase the first render shows (the wait for the preview or already its answer), the frame is the same.
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        var cut = RenderDialog();

        AssertStableFrame(cut);
    }

    [Fact]
    public void PreviewFailedPhase_HasTheStableFrame()
    {
        _handler.SetResponse(HttpMethod.Get, "api/projects/7/deletion-preview", HttpStatusCode.InternalServerError);
        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".project-deletion-calculating")));
        AssertStableFrame(cut);
    }

    [Fact]
    public void CountsPhase_HasTheStableFrame()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".project-deletion-counts")));
        AssertStableFrame(cut);
    }

    [Fact]
    public void ReportPhase_HasTheStableFrame()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".project-deletion-counts")));
        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-submit").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".project-deletion-outcome")), TimeSpan.FromSeconds(30));
        AssertStableFrame(cut);
    }
}
