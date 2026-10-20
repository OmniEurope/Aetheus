// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>What the port registry's actions do: allocate, reserve, convert an observed listener, release
/// a reservation and queue a listener scan. Each sends its request, then either reloads the registry with a
/// success toast, or keeps the page as it is and shows why (the API's own message when it gave one).</summary>
public sealed class ServerPortsSectionActionTests : BunitContext
{
    private const string HolderField = "#oe-pages-servers-serverdetailsections-serverportssection-2";
    private const string PortField = "#oe-pages-servers-serverdetailsections-serverportssection-numeric-1";
    private static readonly DateTime Declared = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerPortsSectionActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto> { new() { Id = 7, Name = "Portfolio" } });
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/range", new PortRangeDto
        {
            From = PortRegistryLimits.DefaultRangeFrom,
            To = PortRegistryLimits.DefaultRangeTo,
            IsExplicit = false
        });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private int RegistryLoads => _handler.Requests.Count(request => request.Method == "GET" && request.Url.EndsWith("/ports", StringComparison.Ordinal));

    private IRenderedComponent<ServerPortsSection> Render(params PortReservationDto[] rows)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "/ports", rows.ToList());
        return Render<ServerPortsSection>(parameters => parameters
            .Add(c => c.ServerId, 1)
            .Add(c => c.PortObservationAvailable, true)
            .Add(c => c.AgentOnline, true));
    }

    private static PortReservationDto Reserved(int id, int port, string holder) => new()
    {
        Id = id,
        ServerId = 1,
        Port = port,
        OwnerLabel = holder,
        Source = PortReservationSource.Manual,
        DeclaredAt = Declared,
        UpdatedAt = Declared
    };

    private static PortReservationDto Observed(int id, int port, string holder) => new()
    {
        Id = id,
        ServerId = 1,
        Port = port,
        OwnerLabel = holder,
        Source = PortReservationSource.Observed,
        DeclaredAt = Declared,
        UpdatedAt = Declared,
        ObservedAt = Declared
    };

    private static ApiError Refusal(string? message) => new() { Message = message ?? string.Empty };

    private bool Toasted(OmniSeverity severity, string key) =>
        Services.Toasts().Any(toast => toast.Severity == severity && (toast.Summary == key || toast.Detail == key));

    private static void FillHolder(IRenderedComponent<ServerPortsSection> cut, string holder) =>
        cut.Find(HolderField).Input(holder);

    private static AngleSharp.Dom.IElement ButtonLabelled(IRenderedComponent<ServerPortsSection> cut, string label) =>
        cut.FindAll("button").First(button => button.TextContent.Contains(label, StringComparison.Ordinal)
            || button.GetAttribute("title") == label || button.GetAttribute("aria-label") == label);

    [Fact]
    public void Allocate_SendsTheHolderAndReloadsTheRegistry()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/allocate", new List<int> { 10001, 10002 });
        var cut = Render();
        var loadsBefore = RegistryLoads;
        FillHolder(cut, "portfolio");

        cut.FindMenuItem("PortAllocate")!.Click(); // Allocate sits in the "More actions" menu (STD-BTN detection 11).

        cut.WaitForAssertion(() => Assert.True(Toasted(OmniSeverity.Success, "PortAllocated")));
        var post = _handler.RequestDetails.Single(request => request.Method == "POST" && request.Url.EndsWith("/ports/allocate", StringComparison.Ordinal));
        Assert.Contains("portfolio", post.Body, StringComparison.Ordinal);
        Assert.Equal(loadsBefore + 1, RegistryLoads);
        Assert.Empty(cut.FindAll(".omni-alert"));
    }

    [Theory]
    [InlineData("Only 2 free port(s) are left in the window.", "Only 2 free port(s) are left in the window.")]
    [InlineData("", "PortAllocateFailed")]
    public void Allocate_Refused_ShowsTheApiMessage_OrTheGenericOne(string apiMessage, string expectedText)
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/allocate", Refusal(apiMessage), HttpStatusCode.Conflict);
        var cut = Render();
        var loadsBefore = RegistryLoads;
        FillHolder(cut, "portfolio");

        cut.FindMenuItem("PortAllocate")!.Click(); // Allocate sits in the "More actions" menu (STD-BTN detection 11).

        cut.WaitForAssertion(() => Assert.Contains(expectedText, cut.Find(".omni-alert").TextContent, StringComparison.Ordinal));
        Assert.False(Toasted(OmniSeverity.Success, "PortAllocated"));
        Assert.Equal(loadsBefore, RegistryLoads);
    }

    [Fact]
    public void Add_ReservesThePort_ClearsTheHolder_AndReloadsTheRegistry()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports", Reserved(5, 10031, "portfolio"));
        var cut = Render();
        var loadsBefore = RegistryLoads;
        cut.Find(PortField).Change("10031");
        FillHolder(cut, "portfolio");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Toasted(OmniSeverity.Success, "PortReservationAdded")));
        var post = _handler.RequestDetails.Single(request => request.Method == "POST" && request.Url.EndsWith("/ports", StringComparison.Ordinal));
        Assert.Contains("10031", post.Body, StringComparison.Ordinal);
        Assert.Contains("portfolio", post.Body, StringComparison.Ordinal);
        Assert.Equal(loadsBefore + 1, RegistryLoads);
        Assert.Equal(string.Empty, cut.Find(HolderField).GetAttribute("value") ?? string.Empty);
    }

    [Theory]
    [InlineData("Port 10031 is already registered to 'other'.", "Port 10031 is already registered to 'other'.")]
    [InlineData("", "PortReservationFailed")]
    public void Add_Refused_NamesTheCurrentHolder_OrFailsGenerically(string apiMessage, string expectedText)
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports", Refusal(apiMessage), HttpStatusCode.Conflict);
        var cut = Render();
        cut.Find(PortField).Change("10031");
        FillHolder(cut, "portfolio");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(expectedText, cut.Find(".omni-alert").TextContent, StringComparison.Ordinal));
        Assert.False(Toasted(OmniSeverity.Success, "PortReservationAdded"));
    }

    [Fact]
    public void Convert_TurnsAnObservedListenerIntoAReservationOfTheSamePortAndHolder()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports", Reserved(9, 9000, "grafana"));
        var cut = Render(Observed(9, 9000, "grafana"));
        var loadsBefore = RegistryLoads;

        ButtonLabelled(cut, "PortConvertToReservation").Click();

        cut.WaitForAssertion(() => Assert.True(Toasted(OmniSeverity.Success, "PortReservationAdded")));
        var post = _handler.RequestDetails.Single(request => request.Method == "POST" && request.Url.EndsWith("/ports", StringComparison.Ordinal));
        Assert.Contains("9000", post.Body, StringComparison.Ordinal);
        Assert.Contains("grafana", post.Body, StringComparison.Ordinal);
        Assert.Equal(loadsBefore + 1, RegistryLoads);
    }

    [Theory]
    [InlineData("Port 9000 belongs to 'nginx'.", "Port 9000 belongs to 'nginx'.")]
    [InlineData("", "PortReservationFailed")]
    public void Convert_Refused_ShowsWhy_AndReloadsNothing(string apiMessage, string expectedText)
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports", Refusal(apiMessage), HttpStatusCode.Conflict);
        var cut = Render(Observed(9, 9000, "grafana"));
        var loadsBefore = RegistryLoads;

        ButtonLabelled(cut, "PortConvertToReservation").Click();

        cut.WaitForAssertion(() => Assert.Contains(expectedText, cut.Find(".omni-alert").TextContent, StringComparison.Ordinal));
        Assert.Equal(loadsBefore, RegistryLoads);
    }

    [Fact]
    public void Release_Confirmed_DeletesTheReservationAndReloads()
    {
        _handler.SetResponse(HttpMethod.Delete, "/ports/3", HttpStatusCode.NoContent);
        Dialog.ConfirmResult = true;
        var cut = Render(Reserved(3, 15432, "postgres"));
        var loadsBefore = RegistryLoads;

        ButtonLabelled(cut, "PortRelease").Click();

        cut.WaitForAssertion(() => Assert.True(Toasted(OmniSeverity.Success, "PortReservationReleased")));
        Assert.Contains(_handler.Requests, request => request.Method == "DELETE" && request.Url.EndsWith("/ports/3", StringComparison.Ordinal));
        Assert.Equal("PortReleaseConfirm", Dialog.LastConfirmMessage);
        Assert.Equal(loadsBefore + 1, RegistryLoads);
    }

    [Fact]
    public void Release_NotConfirmed_SendsNothing()
    {
        Dialog.ConfirmResult = false;
        var cut = Render(Reserved(3, 15432, "postgres"));

        ButtonLabelled(cut, "PortRelease").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Dialog.OpenCount));
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "DELETE");
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public void Release_RefusedByTheServer_RaisesAnErrorAndDoesNotReload()
    {
        _handler.SetResponse(HttpMethod.Delete, "/ports/3", HttpStatusCode.InternalServerError);
        Dialog.ConfirmResult = true;
        var cut = Render(Reserved(3, 15432, "postgres"));
        var loadsBefore = RegistryLoads;

        ButtonLabelled(cut, "PortRelease").Click();

        cut.WaitForAssertion(() => Assert.True(Toasted(OmniSeverity.Danger, "PortReservationFailed")));
        Assert.False(Toasted(OmniSeverity.Success, "PortReservationReleased"));
        Assert.Equal(loadsBefore, RegistryLoads);
    }

    [Fact]
    public void Scan_QueuesTheTask_AndSaysTheResultIsNotThereYet()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/observe", new ServerTaskDto { Id = 4 });
        var cut = Render(Reserved(3, 15432, "postgres"));

        ButtonLabelled(cut, "PortScanRefresh").Click();

        cut.WaitForAssertion(() => Assert.True(Toasted(OmniSeverity.Success, "PortScanQueued")));
        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith("/ports/observe", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".omni-alert"));
    }

    [Theory]
    [InlineData("The agent does not support port observation.", "The agent does not support port observation.")]
    [InlineData("", "PortScanFailed")]
    public void Scan_Refused_ShowsTheCapabilityMessage_OrTheGenericOne(string apiMessage, string expectedText)
    {
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/observe", Refusal(apiMessage), HttpStatusCode.Conflict);
        var cut = Render(Reserved(3, 15432, "postgres"));

        ButtonLabelled(cut, "PortScanRefresh").Click();

        cut.WaitForAssertion(() => Assert.Contains(expectedText, cut.Find(".omni-alert").TextContent, StringComparison.Ordinal));
        Assert.False(Toasted(OmniSeverity.Success, "PortScanQueued"));
    }
}
