// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Services;

/// <summary>The dependency page comes from the paged endpoint; against an older backend that only has
/// the whole graph, the same filters and paging are applied in the browser.</summary>
public sealed class PipelineDependencyPageTests
{
    private static PipelineDependencyDto Dep(int id, string name, int? project, string? projectName, PipelineTriggerType trigger) =>
        new() { Id = id, Name = name, ProjectId = project, ProjectName = projectName, TriggerType = trigger };

    private static readonly PipelineDependencyGroupsDto Graph = new()
    {
        Parents = [Dep(1, "deploy", 5, "Shop", PipelineTriggerType.Manual), Dep(2, "build", 5, "Shop", PipelineTriggerType.Webhook)],
        Leaves = [Dep(3, "nightly", 6, "Ops", PipelineTriggerType.Schedule), Dep(4, "audit", null, null, PipelineTriggerType.Manual)],
    };

    [Fact]
    public void Legacy_WithoutFilter_PagesEveryPipelineByName()
    {
        var page = PipelinesApi.PageLegacyDependencies(Graph, page: 2, pageSize: 2, null, null, null, null);

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(["deploy", "nightly"], page.Items.Select(item => item.Name));
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
    }

    [Theory]
    [InlineData("shop", null, null, new[] { 2, 1 })]
    [InlineData("NIGHT", null, null, new[] { 3 })]
    [InlineData(null, PipelineTriggerType.Manual, null, new[] { 4, 1 })]
    [InlineData(null, null, 5, new[] { 2, 1 })]
    public void Legacy_AppliesSearchTriggerAndProject(string? search, PipelineTriggerType? trigger, int? project, int[] expected)
    {
        var page = PipelinesApi.PageLegacyDependencies(Graph, 1, 10, search, trigger, project, null);

        Assert.Equal(expected, page.Items.Select(item => item.Id));
        Assert.Equal(expected.Length, page.TotalCount);
    }

    [Fact]
    public void Legacy_ForAServer_KeepsOnlyItsPipelines()
    {
        var page = PipelinesApi.PageLegacyDependencies(Graph, 1, 10, null, null, null, new HashSet<int> { 3, 4 });

        Assert.Equal([4, 3], page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task AnOlderBackend_IsReadThroughTheWholeGraphAndTheServerPipelines()
    {
        var handler = new BunitTestHelper.TestHandler();
        handler.SetResponse("api/pipelines/dependencies/page", HttpStatusCode.NotFound);
        handler.SetJsonResponse("api/pipelines/dependencies", Graph);
        handler.SetJsonResponse("api/servers/9/pipelines", new List<PipelineDto> { new() { Id = 1 }, new() { Id = 3 } });
        var api = new PipelinesApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var page = await api.GetPipelineDependencyPageAsync(1, 10, serverId: 9, ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal([1, 3], page.Items.Select(item => item.Id));
        Assert.Contains(handler.Requests, request => request.Url.Contains("serverId=9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACurrentBackend_AnswersThePageItself()
    {
        var handler = new BunitTestHelper.TestHandler();
        handler.SetJsonResponse("api/pipelines/dependencies/page", new PaginatedResult<PipelineDependencyDto>
        {
            Items = [Dep(7, "release", 5, "Shop", PipelineTriggerType.Manual)],
            TotalCount = 1,
            Page = 1,
            PageSize = 10,
        });
        var api = new PipelinesApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var page = await api.GetPipelineDependencyPageAsync(
            1, 10, "rel", PipelineTriggerType.Manual, projectId: 5, ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(7, Assert.Single(page.Items).Id);
        var request = Assert.Single(handler.Requests).Url;
        Assert.Contains("triggerType=Manual", request, StringComparison.Ordinal);
        Assert.Contains("projectId=5", request, StringComparison.Ordinal);
    }
}
