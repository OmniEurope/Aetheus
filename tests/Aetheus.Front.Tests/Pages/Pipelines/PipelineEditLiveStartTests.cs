// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>The pipeline page is drawn from its HTTP reads: the live-update hubs are a refresh channel and
/// never stand between a loaded pipeline and the screen. Seen in the E2E run of 2026-11-02: the page stayed
/// on "Loading…" with every request answered and none pending, because the loader was only cleared in
/// the render that follows the whole load, hub start included.</summary>
public sealed class PipelineEditLiveStartTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineEditLiveStartTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<HubConnectionFactory>(sp => new BunitTestHelper.BlockingHubConnectionFactory(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetRequiredService<AuthStateProvider>()));
    }

    [Fact]
    public void ALiveConnectionThatNeverAnswers_DoesNotKeepALoadedPipelineOnItsLoader()
    {
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "Deploy Prod",
            YamlDefinition = "name: deploy\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual
        });
        _handler.SetJsonResponse("api/pipelines/5/runs", new PaginatedResult<PipelineRunDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        // The last read answers after the page has drawn its loader, as every read does in the browser.
        var source = new TaskCompletionSource<PipelineSourceDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/5/source", ct => source.Task.WaitAsync(ct));

        var cut = Render<PipelineEdit>(parameters => parameters.Add(page => page.Id, 5));
        Assert.True(Services.GetRequiredService<Aetheus.Front.Components.Shared.BusyVeil>().IsBusy, "the page holds the content-area veil while its source is read");

        source.SetResult(new PipelineSourceDto());

        cut.WaitForAssertion(() => Assert.Contains("Deploy Prod", cut.Markup), TimeSpan.FromSeconds(30));
        Assert.False(Services.GetRequiredService<Aetheus.Front.Components.Shared.BusyVeil>().IsBusy);
    }
}
