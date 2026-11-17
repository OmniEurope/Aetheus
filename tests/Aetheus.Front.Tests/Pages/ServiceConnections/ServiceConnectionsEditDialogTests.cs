// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.ServiceConnections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.ServiceConnections;

/// <summary>
/// The service connections page's create and edit dialog: it opens on no connection for a creation and
/// on the row's connection for an edit; a saved dialog drops the test badges (the credentials may have
/// changed) and reads the list again, a dismissed one leaves the page as it was.
/// </summary>
public sealed class ServiceConnectionsEditDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServiceConnectionsEditDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/service-connections", new PaginatedResult<ServiceConnectionDto>
        {
            Items = [new ServiceConnectionDto { Id = 1, Name = "GitHub prod", Type = ServiceConnectionType.GitHub, Url = "https://github.com" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/service-connections/1/test",
            new ServiceConnectionTestResultDto { Status = ServiceConnectionTestStatus.Valid, Message = "ok" });
    }

    private IRenderedComponent<Aetheus.Front.Components.ServiceConnections.ServiceConnections> RenderTested()
    {
        var cut = Render<Aetheus.Front.Components.ServiceConnections.ServiceConnections>();
        cut.WaitForState(() => cut.Markup.Contains("GitHub prod", StringComparison.Ordinal));
        cut.FindMenuItem("TestConnection")!.Click();
        cut.WaitForState(() => cut.Markup.Contains("Enum_ServiceConnectionTestStatus_Valid", StringComparison.Ordinal));
        return cut;
    }

    private int ListReads() =>
        _handler.Requests.Count(request => request.Method == "GET"
            && request.Url.Contains("api/service-connections?", StringComparison.Ordinal));

    [Fact]
    public async Task ASavedCreation_DropsTheTestBadges_AndReadsTheListAgain()
    {
        var cut = RenderTested();
        // R-10: the dialog closes with the connection the server saved, not with a bare "true".
        _dialog.OpenResult = new ServiceConnectionDto { Id = 2, Name = "GitLab dev", Type = ServiceConnectionType.GitLab };
        var readsBefore = ListReads();

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Create").ClickAsync(new());

        Assert.Equal(typeof(ServiceConnectionEditDialog), _dialog.LastComponent);
        Assert.Equal("Create", _dialog.LastTitle);
        Assert.Null(Assert.Contains("ConnectionId", _dialog.LastParameters!));
        cut.WaitForAssertion(() => Assert.True(ListReads() > readsBefore));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Enum_ServiceConnectionTestStatus_Valid", cut.Markup, StringComparison.Ordinal));
    }

    private TaskCompletionSource<PaginatedResult<ServiceConnectionDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<ServiceConnectionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/service-connections?", _ => gate.Task);
        return gate;
    }

    private static AetheusDataGrid<ServiceConnectionDto> GridOf(IRenderedComponent<Aetheus.Front.Components.ServiceConnections.ServiceConnections> cut) =>
        cut.FindComponent<AetheusDataGrid<ServiceConnectionDto>>().Instance;

    /// <summary>R-10: the saved row is on screen before the list is read again, and that read empties nothing.</summary>
    [Fact]
    public async Task ASavedCreation_ShowsTheNewRowAtOnce_BeforeTheListIsReadAgain()
    {
        var cut = RenderTested();
        var gate = HoldTheNextListRead();
        var created = new ServiceConnectionDto { Id = 2, Name = "GitLab dev", Type = ServiceConnectionType.GitLab };
        _dialog.OpenResult = created;

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Create").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("GitLab dev", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("GitHub prod", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("thead > tr.omni-data-grid__progress .omni-loading-bar"));

        gate.SetResult(new PaginatedResult<ServiceConnectionDto>
        {
            Items = [new ServiceConnectionDto { Id = 1, Name = "GitHub prod", Type = ServiceConnectionType.GitHub }, created],
            TotalCount = 2
        });
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.Contains("GitLab dev", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedConnection_GoesAtOnce_AndAFailedDeleteKeepsTheRow()
    {
        var cut = RenderTested();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/service-connections/1", System.Net.HttpStatusCode.InternalServerError);

        await cut.FindAll("button[title='Delete']")[0].ClickAsync(new());

        Assert.Contains(_handler.Requests, request => request.Method == "DELETE");
        Assert.Contains("GitHub prod", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/service-connections/1", System.Net.HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        await cut.FindAll("button[title='Delete']")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("GitHub prod", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);

        gate.SetResult(new PaginatedResult<ServiceConnectionDto> { Items = [], TotalCount = 0 });
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.DoesNotContain("GitHub prod", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADismissedEdit_OpensOnTheRowsConnection_AndChangesNothing()
    {
        var cut = RenderTested();
        _dialog.OpenResult = null;
        var readsBefore = ListReads();

        await cut.FindAll("button[title='Edit']")[0].ClickAsync(new());

        Assert.Equal(typeof(ServiceConnectionEditDialog), _dialog.LastComponent);
        Assert.Equal("Edit", _dialog.LastTitle);
        Assert.Equal(1, Assert.Contains("ConnectionId", _dialog.LastParameters!));
        Assert.Equal(readsBefore, ListReads());
        Assert.Contains("Enum_ServiceConnectionTestStatus_Valid", cut.Markup, StringComparison.Ordinal);
    }
}
