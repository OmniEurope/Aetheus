// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Recette R2-087 (decided 2026-10-03): a page come back to shows at once what it showed, without the
/// page-wide loader, then refreshes behind it; the loader stays for a first visit.
/// </summary>
public sealed class PageReturnStateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PageReturnStateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void PageStateCache_KeepsOneStatePerAddressAndType_AndForgetsItWithTheSession()
    {
        var cache = Services.GetRequiredService<PageStateCache>();
        var pipeline = new PipelineDto { Id = 5, Name = "Deploy Prod" };

        cache.Remember(cache.Begin(), "pipelines/5", pipeline);

        Assert.True(cache.TryRestore<PipelineDto>("pipelines/5", out var restored));
        Assert.Same(pipeline, restored);
        Assert.False(cache.TryRestore<PipelineDto>("pipelines/6", out _));
        Assert.False(cache.TryRestore<PipelineRunDto>("pipelines/5", out _));
        // The layout clears this cache on sign-out, on a rejected token and on an organization switch.
        Services.GetRequiredService<ListCacheService>().Clear();
        Assert.False(cache.TryRestore<PipelineDto>("pipelines/5", out _));
    }

    [Fact]
    public void PipelinePage_OnReturn_ShowsItsLastStateWithoutTheLoader_ThenRefreshes()
    {
        SetupPipeline("Deploy Prod");
        var firstSource = HoldSource("api/pipelines/5/source");
        var first = Render<PipelineEdit>(parameters => parameters.Add(page => page.Id, 5));
        // First visit: nothing to show yet, the page-wide loader.
        Assert.NotEmpty(first.FindAll(".aetheus-loader"));
        firstSource.SetResult(new PipelineSourceDto());
        first.WaitForState(() => first.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));

        SetupPipeline("Deploy Prod renamed");
        var refresh = HoldSource("api/pipelines/5/source");
        var back = Render<PipelineEdit>(parameters => parameters.Add(page => page.Id, 5));

        back.WaitForState(() => back.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        Assert.Empty(back.FindAll(".aetheus-loader"));
        Assert.DoesNotContain("Deploy Prod renamed", back.Markup);
        refresh.SetResult(new PipelineSourceDto());
        back.WaitForState(() => back.Markup.Contains("Deploy Prod renamed"), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RunPage_OnReturn_ShowsTheRunWithoutTheLoader_ThenRefreshes()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        var firstLoad = new TaskCompletionSource<PipelineRunDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/runs/77", ct => firstLoad.Task.WaitAsync(ct));
        var first = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 77));
        // First visit: only the page-wide loader until the run is read.
        Assert.NotEmpty(first.FindAll(".aetheus-loader"));
        Assert.DoesNotContain("Nightly", first.Markup);
        firstLoad.SetResult(Run("Nightly"));
        first.WaitForState(() => first.Markup.Contains("Nightly"), TimeSpan.FromSeconds(2));

        var refresh = new TaskCompletionSource<PipelineRunDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/runs/77", ct => refresh.Task.WaitAsync(ct));
        var back = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 77));

        // The run is shown while it is read again: the loader branch renders nothing but the loader.
        back.WaitForState(() => back.Markup.Contains("Nightly"), TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("Nightly renamed", back.Markup);
        refresh.SetResult(Run("Nightly renamed"));
        back.WaitForState(() => back.Markup.Contains("Nightly renamed"), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ServerPages_OnReturn_ShowTheLastKnownServerWhileItIsReadAgain()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/monitoring/servers/", new List<ServerMetricDto>());
        _handler.SetJsonResponse("api/servers/9", new ServerDetailDto { Id = 9, Name = "vps-1" });
        var firstVisit = CreateServerLoader();
        await firstVisit.EnsureLoadedAsync(9, Xunit.TestContext.Current.CancellationToken);
        await firstVisit.DisposeAsync();

        var refresh = new TaskCompletionSource<ServerDetailDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/servers/9", ct => refresh.Task.WaitAsync(ct));
        var back = CreateServerLoader();
        var load = back.EnsureLoadedAsync(9, Xunit.TestContext.Current.CancellationToken);

        Assert.False(load.IsCompleted);
        Assert.Equal("vps-1", back.Server?.Name);
        refresh.SetResult(new ServerDetailDto { Id = 9, Name = "vps-1 renamed" });
        await load.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("vps-1 renamed", back.Server?.Name);
        await back.DisposeAsync();
    }

    private ServerDetailLoader CreateServerLoader() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        Services.GetRequiredService<NavigationManager>(),
        NullLogger<ServerDetailLoader>.Instance,
        Services.GetRequiredService<PageStateCache>());

    private static PipelineRunDto Run(string pipelineName) => new()
    {
        Id = 77,
        PipelineId = 5,
        PipelineName = pipelineName,
        Status = PipelineStatus.Success
    };

    private void SetupPipeline(string name)
    {
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = name,
            YamlDefinition = "name: deploy\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual
        });
        _handler.SetJsonResponse("api/pipelines/5/runs", new PaginatedResult<PipelineRunDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
    }

    private TaskCompletionSource<PipelineSourceDto> HoldSource(string url)
    {
        var source = new TaskCompletionSource<PipelineSourceDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, url, ct => source.Task.WaitAsync(ct));
        return source;
    }
}
