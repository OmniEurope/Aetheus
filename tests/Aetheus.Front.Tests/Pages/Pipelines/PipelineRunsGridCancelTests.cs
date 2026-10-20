// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>A live run can be stopped from the runs table once confirmed; the answer of the server is
/// reported, and only an accepted cancellation tells the host to refresh.</summary>
public sealed class PipelineRunsGridCancelTests : BunitContext
{
    private const string CancelUrl = "api/pipelines/runs/41/cancel";
    private readonly BunitTestHelper.TestHandler _handler;
    private int _refreshes;

    public PipelineRunsGridCancelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    private OmniOverlayService Toasts => Services.GetRequiredService<OmniOverlayService>();

    private IRenderedComponent<PipelineRunsGrid> RenderGrid(PipelineStatus status = PipelineStatus.Running) =>
        Render<PipelineRunsGrid>(p => p
            .Add(x => x.Items, new List<PipelineRunDto>
            {
                new() { Id = 41, PipelineId = 2, PipelineName = "ci", Status = status, StartedAt = DateTime.Now.AddMinutes(-2) },
            })
            .Add(x => x.OnRunCancelled, () => _refreshes++));

    private static Task ClickCancelAsync(IRenderedComponent<PipelineRunsGrid> cut) =>
        cut.FindAll("button").Single(button => button.GetAttribute("title") == "CancelRun").ClickAsync(new());

    [Fact]
    public async Task AConfirmedCancel_IsReportedAndRefreshesTheHost()
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Post, CancelUrl, HttpStatusCode.OK);
        var cut = RenderGrid();

        await ClickCancelAsync(cut);

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.Contains(CancelUrl, StringComparison.Ordinal));
        Assert.Equal(OmniSeverity.Info, Assert.Single(Toasts.Toasts()).Severity);
        Assert.Equal(1, _refreshes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "PipelineRunForbidden")]
    [InlineData(HttpStatusCode.InternalServerError, "Error")]
    public async Task ARefusedCancel_IsReportedWithoutRefreshing(HttpStatusCode status, string detail)
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Post, CancelUrl, status);
        var cut = RenderGrid();

        await ClickCancelAsync(cut);

        var toast = Assert.Single(Toasts.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Equal(detail, toast.Detail);
        Assert.Equal(0, _refreshes);
    }

    [Fact]
    public async Task ADeclinedCancel_SendsNothing()
    {
        Dialog.ConfirmResult = false;
        var cut = RenderGrid(PipelineStatus.WaitingForApproval);

        await ClickCancelAsync(cut);

        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST");
    }

    [Fact]
    public void AFinishedRun_OffersNoCancel()
    {
        var cut = RenderGrid(PipelineStatus.Success);

        Assert.DoesNotContain(cut.FindAll("button"), button => button.GetAttribute("title") == "CancelRun");
    }
}
