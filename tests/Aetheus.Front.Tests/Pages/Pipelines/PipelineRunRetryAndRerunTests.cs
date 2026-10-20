// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// From a finished run's header: "retry failed steps" retries them and reloads the run only when the
/// backend accepted, and "rerun at the same commit" opens the new run.
/// </summary>
public sealed class PipelineRunRetryAndRerunTests : BunitContext
{
    private const string RetryUrl = "api/pipelines/runs/40/retry-failed";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunRetryAndRerunTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", HttpStatusCode.NoContent);
        _handler.SetJsonResponse("api/pipelines/runs/40", new PipelineRunDto
        {
            Id = 40,
            PipelineId = 5,
            PipelineName = "release-fast",
            Status = PipelineStatus.Failed,
            StartedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 3, 10, 5, 0, DateTimeKind.Utc)
        });
    }

    private int RunReads() => _handler.Requests.Count(request => request.Method == "GET" && request.Url.EndsWith("api/pipelines/runs/40", StringComparison.Ordinal));

    private IRenderedComponent<PipelineRunHeader> RenderHeader()
    {
        var cut = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 40));
        cut.WaitForState(() => cut.Markup.Contains("release-fast", StringComparison.Ordinal));
        return cut.FindComponent<PipelineRunHeader>();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public async Task RetryFailedSteps_ReloadsTheRunOnlyWhenTheRetryWasAccepted(HttpStatusCode status, bool reloads)
    {
        _handler.SetJsonResponse(HttpMethod.Post, RetryUrl, new PipelineRunDto { Id = 40 }, status);
        var header = RenderHeader();
        var readsBefore = RunReads();

        await header.InvokeAsync(() => header.Instance.RetryFailedRequested.InvokeAsync());

        Assert.Single(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith(RetryUrl, StringComparison.Ordinal));
        Assert.Equal(reloads, RunReads() > readsBefore);
    }

    [Fact]
    public async Task RerunAtTheSameCommit_OpensTheNewRun()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/runs/40/rerun", new PipelineRunDto { Id = 41 });
        var header = RenderHeader();

        await header.InvokeAsync(() => header.Instance.RerunRequested.InvokeAsync("sameCommit"));

        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/pipelines/runs/40/rerun?mode=SnapshotSameCommit", StringComparison.Ordinal));
        Assert.EndsWith("/pipelines/runs/41", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }
}
