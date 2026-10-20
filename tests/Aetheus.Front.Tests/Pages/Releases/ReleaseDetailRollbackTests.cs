// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Releases;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Releases;

/// <summary>A rollback is asked through its dialog, with the project's pipelines to run it, and queued
/// only when the reader submits the dialog.</summary>
public sealed class ReleaseDetailRollbackTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ReleaseDetailRollbackTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases/7", new ReleaseDto
        {
            Id = 7,
            ProjectId = 1,
            ProjectName = "Demo",
            Version = "v2.3.0",
            BranchName = "main",
            DetectedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _handler.SetJsonResponse("api/releases/7/rollback-preview", new ReleaseRollbackPreviewDto { CanRollback = true, TargetVersion = "v2.2.0" });
        _handler.SetPaginatedJsonResponse("api/pipelines", new[] { new PipelineDto { Id = 3, Name = "deploy" } });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private async Task<IRenderedComponent<ReleaseDetail>> ClickRollbackAsync()
    {
        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));
        cut.WaitForState(() => cut.Markup.Contains("Rollback", StringComparison.Ordinal));
        await cut.FindAll("button").Single(button => button.TextContent.Contains("Rollback", StringComparison.Ordinal)).ClickAsync(new());
        return cut;
    }

    [Fact]
    public async Task ASubmittedDialog_QueuesTheRollback()
    {
        Dialog.OpenResult = new RollbackReleaseRequest { PipelineId = 3 };
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/7/rollback", new ReleaseRollbackDto { Id = 11, SourceReleaseId = 7 });

        await ClickRollbackAsync();

        Assert.Equal(typeof(RollbackReleaseDialog), Dialog.LastComponent);
        Assert.Equal("v2.2.0", Dialog.LastParameters!["TargetVersion"]);
        Assert.Contains(_handler.RequestDetails, request => request.Method == "POST"
            && request.Url.Contains("api/releases/7/rollback", StringComparison.Ordinal)
            && request.Body!.Contains("\"pipelineId\":3", StringComparison.Ordinal));
        Assert.Contains(Services.GetRequiredService<OmniOverlayService>().Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task ADismissedDialog_QueuesNothing()
    {
        Dialog.OpenResult = null;

        await ClickRollbackAsync();

        Assert.Equal(1, Dialog.OpenCount);
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST");
    }
}
