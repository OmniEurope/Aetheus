// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ServersPage = Aetheus.Front.Components.Servers.Servers;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// "Update all agents" shows what it would do before doing it and queues the update only once
/// confirmed; the fleet grid's export reads the page it is asked for, in its sort.
/// </summary>
public sealed class ServersUpdateAllAndExportTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServersUpdateAllAndExportTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 1, Name = "web-01", Hostname = "10.0.0.1", Status = ServerStatus.Online }],
            TotalCount = 1
        });
    }

    private IRenderedComponent<ServersPage> RenderPage()
    {
        var cut = Render<ServersPage>();
        cut.WaitForAssertion(() => Assert.Contains("web-01", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private int UpdatePosts => _handler.Requests.Count(request =>
        request.Method == "POST" && request.Url.EndsWith("api/servers/agent/update-all", StringComparison.Ordinal));

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void UpdateAll_ShowsThePreviewThenQueuesOnlyOnceConfirmed(bool confirmed, int expectedPosts)
    {
        _handler.SetJsonResponse("api/servers/agent/update-all-preview", new AgentUpdateAllPreviewDto
        {
            TargetVersion = "1.0.1300",
            AffectedCount = 4,
            AlreadyUpToDateCount = 2,
            OfflineCount = 1,
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/agent/update-all", new AgentUpdateAllResponse { QueuedCount = 4 });
        _dialog.ConfirmResult = confirmed;
        var cut = RenderPage();

        cut.FindMenuItem("UpdateAllAgents")!.Click();

        cut.WaitForAssertion(() => Assert.Equal("UpdateAllAgents", _dialog.LastTitle));
        Assert.Equal("UpdateAllAgentsPreview", _dialog.LastConfirmMessage);
        cut.WaitForAssertion(() => Assert.Equal(expectedPosts, UpdatePosts));
    }

    [Fact]
    public void UpdateAll_WithoutAPreview_AsksNothingAndQueuesNothing()
    {
        _handler.SetResponse("api/servers/agent/update-all-preview", HttpStatusCode.InternalServerError);
        var cut = RenderPage();

        cut.FindMenuItem("UpdateAllAgents")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.EndsWith("update-all-preview", StringComparison.Ordinal)));
        Assert.Null(_dialog.LastConfirmMessage);
        Assert.Equal(0, UpdatePosts);
    }

    [Fact]
    public async Task TheExport_ReadsTheRequestedPageInTheGridsSort()
    {
        var cut = RenderPage();
        var grid = cut.FindComponent<AetheusDataGrid<ServerDto>>().Instance;

        var page = await cut.InvokeAsync(() => grid.ExportRead!(new GridLoadArgs
        {
            Skip = 40,
            Top = 20,
            Sorts = [new GridSortDescriptor("Name", GridSortOrder.Ascending)],
        }));

        Assert.Equal("web-01", Assert.Single(page.Items).Name);
        var url = _handler.Requests.Last(request => request.Url.Contains("api/servers?", StringComparison.Ordinal)).Url;
        Assert.Contains("page=3", url, StringComparison.Ordinal);
        Assert.Contains("pageSize=20", url, StringComparison.Ordinal);
        Assert.Contains("sortBy=Name", url, StringComparison.Ordinal);
        Assert.Contains("sortDescending=False", url, StringComparison.Ordinal);
    }
}
