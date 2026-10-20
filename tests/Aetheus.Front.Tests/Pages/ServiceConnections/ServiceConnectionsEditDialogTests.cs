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
        _dialog.OpenResult = true;
        var readsBefore = ListReads();

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Create").ClickAsync(new());

        Assert.Equal(typeof(ServiceConnectionEditDialog), _dialog.LastComponent);
        Assert.Equal("Create", _dialog.LastTitle);
        Assert.Null(Assert.Contains("ConnectionId", _dialog.LastParameters!));
        cut.WaitForAssertion(() => Assert.True(ListReads() > readsBefore));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Enum_ServiceConnectionTestStatus_Valid", cut.Markup, StringComparison.Ordinal));
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
