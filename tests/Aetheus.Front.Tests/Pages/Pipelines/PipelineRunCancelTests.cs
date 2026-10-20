// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The run page stops a run in progress only once the reader confirms, then reloads it; a run
/// already over offers nothing to stop.</summary>
public sealed class PipelineRunCancelTests : BunitContext
{
    private const string CancelUrl = "api/pipelines/runs/40/cancel";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunCancelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", HttpStatusCode.NoContent);
        _handler.SetResponse(HttpMethod.Post, CancelUrl, HttpStatusCode.OK);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private int RunReads() => _handler.Requests.Count(request => request.Method == "GET" && request.Url.EndsWith("api/pipelines/runs/40", StringComparison.Ordinal));

    private bool CancelSent() => _handler.Requests.Any(request => request.Method == "POST" && request.Url.Contains(CancelUrl, StringComparison.Ordinal));

    private async Task<int> RequestCancelAsync(PipelineStatus status, DateTime? completedAt = null)
    {
        _handler.SetJsonResponse("api/pipelines/runs/40", new PipelineRunDto
        {
            Id = 40,
            PipelineName = "release-fast",
            Status = status,
            StartedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = completedAt
        });
        var cut = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 40));
        cut.WaitForState(() => cut.Markup.Contains("release-fast", StringComparison.Ordinal));
        var header = cut.FindComponent<PipelineRunHeader>();
        var readsBefore = RunReads();
        await cut.InvokeAsync(() => header.Instance.CancelRequested.InvokeAsync());
        return readsBefore;
    }

    [Fact]
    public async Task AConfirmedCancel_StopsTheRunAndReloadsIt()
    {
        Dialog.ConfirmResult = true;

        var readsBefore = await RequestCancelAsync(PipelineStatus.Running);

        Assert.True(CancelSent());
        Assert.True(RunReads() > readsBefore, "A cancel reloads the run.");
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Info);
    }

    [Fact]
    public async Task ADeclinedCancel_StopsNothing()
    {
        Dialog.ConfirmResult = false;

        await RequestCancelAsync(PipelineStatus.Running);

        Assert.NotNull(Dialog.LastConfirmMessage);
        Assert.False(CancelSent());
    }

    [Fact]
    public async Task ARunAlreadyOver_IsNotAskedAbout()
    {
        Dialog.ConfirmResult = true;

        await RequestCancelAsync(PipelineStatus.Success, new DateTime(2026, 10, 3, 10, 5, 0, DateTimeKind.Utc));

        Assert.Null(Dialog.LastConfirmMessage);
        Assert.False(CancelSent());
    }
}
