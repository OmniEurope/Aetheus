// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// FRONT-012: the template list cache answers only while it can still be right. Extraction and promotion
/// invalidate it like the other writers, a realtime change re-reads the server, and the cache is bounded
/// so a change made in another session reaches the pages that never listen to the hub.
/// </summary>
public sealed class PipelineTemplatesCacheTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly FakeTimeProvider _time = new();
    private readonly PipelineTemplatesApi _api;
    private int _version = 1;

    public PipelineTemplatesCacheTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/templates", _ => Task.FromResult(
            new List<PipelineTemplateSummaryDto> { new() { Id = 7, Name = "Build Template", Version = _version } }));
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/3/extract-template", new PipelineTemplateDto { Id = 8, Name = "Extracted" });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/3/promote-template", new PipelineTemplateDto { Id = 7, Name = "Build Template" });
        _api = new PipelineTemplatesApi(new HttpClient(_handler) { BaseAddress = new Uri("http://test/") }, _time);
    }

    private int TemplateListReads => _handler.Requests.Count(request =>
        request.Method == "GET" && request.Url.EndsWith("api/pipelines/templates", StringComparison.Ordinal));

    [Fact]
    public async Task ASuccessfulExtraction_InvalidatesTheList()
    {
        await _api.GetPipelineTemplatesAsync();

        await _api.ExtractPipelineTemplateAsync(3, new ExtractPipelineTemplateRequest { TemplateName = "Extracted" }, Xunit.TestContext.Current.CancellationToken);
        await _api.GetPipelineTemplatesAsync();

        Assert.Equal(2, TemplateListReads);
    }

    [Fact]
    public async Task ASuccessfulPromotion_InvalidatesTheList()
    {
        await _api.GetPipelineTemplatesAsync();
        _version = 2;

        await _api.PromotePipelineTemplateAsync(3, new PromotePipelineTemplateRequest { ChangelogEntry = "v2" }, Xunit.TestContext.Current.CancellationToken);
        var templates = await _api.GetPipelineTemplatesAsync();

        Assert.Equal(2, TemplateListReads);
        Assert.Equal(2, Assert.Single(templates).Version);
    }

    [Fact]
    public async Task TheList_IsServedFromTheCacheWithinItsLifetime_AndReReadAfterIt()
    {
        await _api.GetPipelineTemplatesAsync();
        _time.Advance(PipelineTemplatesApi.CacheLifetime - TimeSpan.FromSeconds(1));
        await _api.GetPipelineTemplatesAsync();
        Assert.Equal(1, TemplateListReads);

        // A version promoted from another session: no writer of this client saw it.
        _version = 5;
        _time.Advance(TimeSpan.FromSeconds(2));
        var templates = await _api.GetPipelineTemplatesAsync();

        Assert.Equal(2, TemplateListReads);
        Assert.Equal(5, Assert.Single(templates).Version);
    }

    [Fact]
    public async Task ARealtimeChange_OnTheTemplatesPage_ReReadsTheServerList()
    {
        var cut = Render<PipelineTemplates>();
        cut.WaitForState(() => cut.Markup.Contains("Build Template", StringComparison.Ordinal));
        var readsBefore = TemplateListReads;
        _version = 4;

        await cut.InvokeAsync(() => (Task)typeof(PipelineTemplates)
            .GetMethod("ReloadFromRealtimeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, [])!);

        Assert.Equal(readsBefore + 1, TemplateListReads);
        Assert.Contains("v4", cut.Markup, StringComparison.Ordinal);
    }
}
