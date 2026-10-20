// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The port-registry view exposes reservations and holders while withholding write actions
/// from unauthorized readers.</summary>
public class ServerPortsSectionTests : BunitContext
{
    private static readonly DateTime Declared = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private BunitTestHelper.TestHandler? _handler;

    private static List<PortReservationDto> ThreeReservations() =>
    [
        new()
        {
            Id = 1, ServerId = 1, Port = 10031, OwnerLabel = "portfolio-prod-front",
            ProjectId = 7, ProjectName = "Portfolio", Source = PortReservationSource.Declared,
            DeclaredAt = Declared, UpdatedAt = Declared
        },
        new()
        {
            Id = 2, ServerId = 1, Port = 10041, OwnerLabel = "aetheus-prod-back",
            Source = PortReservationSource.Declared, DeclaredAt = Declared, UpdatedAt = Declared
        },
        new()
        {
            Id = 3, ServerId = 1, Port = 15432, OwnerLabel = "postgres hôte",
            Source = PortReservationSource.Manual, DeclaredAt = Declared, UpdatedAt = Declared
        }
    ];

    /// <summary>Assertions isolate rows because header-filter choices also contain status
    /// labels.</summary>
    private static string Rows(IRenderedComponent<ServerPortsSection> cut) =>
        string.Concat(cut.FindAll("tbody tr").Select(row => row.InnerHtml));

    private IRenderedComponent<ServerPortsSection> RenderWith(
        List<PortReservationDto> reservations,
        bool canWrite = true,
        DateTime? lastScan = null,
        bool? observationAvailable = null,
        bool agentOnline = true)
    {
        _handler = BunitTestHelper.RegisterServices(this);
        var handler = _handler;
        handler.SetJsonResponse(HttpMethod.Get, "/ports", reservations);
        handler.SetJsonResponse(HttpMethod.Get, "/ports/range", new PortRangeDto
        {
            From = PortRegistryLimits.DefaultRangeFrom,
            To = PortRegistryLimits.DefaultRangeTo,
            IsExplicit = false
        });
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto>
        {
            new() { Id = 7, Name = "Portfolio" }
        });

        if (!canWrite)
            Services.GetRequiredService<PermissionService>().SetPermissions(
                [new EffectivePermissionDto
                {
                    ResourceType = ResourceType.Server, ResourceId = null, Permission = Permission.Read
                }],
                isAdmin: false);

        return Render<ServerPortsSection>(parameters => parameters
            .Add(c => c.ServerId, 1)
            .Add(c => c.PortsObservedAt, lastScan)
            .Add(c => c.PortObservationAvailable, observationAvailable)
            .Add(c => c.AgentOnline, agentOnline));
    }

    [Fact]
    public void EmptyRegistry_ShowsEmptyStateAndNoRows()
    {
        var cut = RenderWith([]);

        Assert.Contains("PortRegistryEmptyTitle", cut.Markup);
        Assert.DoesNotContain("PortRelease", cut.Markup);
    }

    [Fact]
    public void ThreeReservations_ShowEveryPortHolderAndSource()
    {
        var cut = RenderWith(ThreeReservations());

        Assert.Contains("10031", cut.Markup);
        Assert.Contains("10041", cut.Markup);
        Assert.Contains("15432", cut.Markup);
        Assert.Contains("portfolio-prod-front", cut.Markup);
        Assert.Contains("Enum_PortReservationSource_Declared", cut.Markup);
        Assert.Contains("Enum_PortReservationSource_Manual", cut.Markup);
    }

    [Fact]
    public void LinkedProject_RendersAsLink_AndUnlinkedRowStaysEmpty()
    {
        var cut = RenderWith(ThreeReservations());

        var links = cut.FindAll("a[href='projects/7']");
        Assert.Single(links);
        Assert.Contains("Portfolio", links[0].TextContent);
    }

    [Fact]
    public void WithoutServerWrite_HidesAddFormAndReleaseButtons()
    {
        var cut = RenderWith(ThreeReservations(), canWrite: false);

        Assert.Contains("PortRegistryReadOnlyHint", cut.Markup);
        Assert.DoesNotContain("PortRelease", cut.Markup);
        // Only the add form carries the project placeholder, so its absence proves the form is gone
        // while the grid (and its own "linked project" column) is still rendered.
        Assert.DoesNotContain("PortNoProject", cut.Markup);
        Assert.Contains("10031", cut.Markup);
    }

    // Listener observations, scan progress and conversion.

    [Fact]
    public void NeverScanned_ShowsUnknownRatherThanClaimingNothingListens()
    {
        var cut = RenderWith(ThreeReservations(), lastScan: null);

        Assert.Contains("PortNeverScanned", cut.Markup);
        Assert.Contains("PortListeningUnknown", Rows(cut));
        Assert.DoesNotContain("PortListeningNo", Rows(cut));
    }

    [Fact]
    public void StaleServerDto_DoesNotSayNeverScannedAboveRowsAScanJustStamped()
    {
        // A heartbeat-reported scan stamps the rows but never refreshes the server DTO the layout
        // loaded, so the parameter is still null while the rows carry the scan time.
        var scan = new DateTime(2026, 9, 12, 11, 50, 0, DateTimeKind.Utc);
        var reservations = ThreeReservations();
        reservations[0] = reservations[0] with { ObservedAt = scan };
        reservations.Add(new PortReservationDto
        {
            Id = 9,
            ServerId = 1,
            Port = 22,
            OwnerLabel = "sshd",
            Source = PortReservationSource.Observed,
            DeclaredAt = scan,
            UpdatedAt = scan,
            ObservedAt = scan
        });

        var cut = RenderWith(reservations, lastScan: null);

        Assert.DoesNotContain("PortNeverScanned", cut.Markup);
        Assert.Contains("PortLastScan", cut.Markup);
        // The API client hands dates back in local time; the instant is what matters.
        Assert.Equal(scan, cut.Instance.LastScanAt?.ToUniversalTime());
        // 10031 was seen by that scan, 10041 and 15432 were not: once the scan is known they read "no".
        Assert.Contains("PortListeningNo", Rows(cut));
        Assert.DoesNotContain("PortListeningUnknown", Rows(cut));
    }

    [Fact]
    public void ScannedClaim_ReadsListeningOnlyWhenItsStampMatchesTheLastScan()
    {
        var scan = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var reservations = ThreeReservations();
        // 10031 was seen by this very scan; 10041 was last seen by an older one and is now stale.
        reservations[0] = reservations[0] with { ObservedAt = scan };
        reservations[1] = reservations[1] with { ObservedAt = scan.AddDays(-2) };

        var cut = RenderWith(reservations, lastScan: scan);

        Assert.Contains("PortListeningYes", Rows(cut));
        Assert.Contains("PortListeningNo", Rows(cut));
        Assert.DoesNotContain("PortNeverScanned", cut.Markup);
        // Exactly one row may read "listening": counting is what pins the stale 10041 to "no".
        Assert.Equal(1, Rows(cut).Split("PortListeningYes").Length - 1);
    }

    [Fact]
    public void ObservedPortAlreadyClaimed_DoesNotProduceASecondRow()
    {
        var scan = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var reservations = ThreeReservations();
        reservations[0] = reservations[0] with { ObservedAt = scan };
        reservations.Add(new PortReservationDto
        {
            Id = 9,
            ServerId = 1,
            Port = 10031,
            OwnerLabel = "docker-proxy",
            Source = PortReservationSource.Observed,
            DeclaredAt = scan,
            UpdatedAt = scan,
            ObservedAt = scan
        });

        var cut = RenderWith(reservations, lastScan: scan);

        // The claim is the row an operator acts on; repeating the port as an observation would double it.
        Assert.DoesNotContain("docker-proxy", cut.Markup);
        Assert.Contains("portfolio-prod-front", cut.Markup);
    }

    // FRONT-018: a TCP claim on a number must not hide the UDP listener observed on the same number.
    [Fact]
    public void TcpClaim_DoesNotHideTheUdpListenerOnTheSameNumber()
    {
        var scan = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var reservations = ThreeReservations();
        reservations[0] = reservations[0] with { ObservedAt = scan };
        reservations.Add(new PortReservationDto
        {
            Id = 9,
            ServerId = 1,
            Port = 10031,
            Protocol = PortRegistryLimits.UdpProtocol,
            OwnerLabel = "udp-listener",
            Source = PortReservationSource.Observed,
            DeclaredAt = scan,
            UpdatedAt = scan,
            ObservedAt = scan
        });

        var cut = RenderWith(reservations, lastScan: scan);

        var rowsOnPort = cut.FindAll("tbody tr").Where(row => row.TextContent.Contains("10031")).ToList();
        Assert.Equal(2, rowsOnPort.Count);
        Assert.Contains(rowsOnPort, row => row.TextContent.Contains("portfolio-prod-front"));
        Assert.Contains(rowsOnPort, row => row.TextContent.Contains("udp-listener")
            && row.TextContent.Contains(PortRegistryLimits.UdpProtocol));
    }

    [Fact]
    public void UdpSighting_ShowsItsProtocolAlongsideTheTcpRows()
    {
        var scan = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var reservations = ThreeReservations();
        reservations.Add(new PortReservationDto
        {
            Id = 9,
            ServerId = 1,
            Port = 53,
            Protocol = PortRegistryLimits.UdpProtocol,
            OwnerLabel = "dnsmasq",
            Source = PortReservationSource.Observed,
            DeclaredAt = scan,
            UpdatedAt = scan,
            ObservedAt = scan
        });

        var cut = RenderWith(reservations, lastScan: scan);

        // Without the protocol column a UDP sighting reads as a duplicate of a TCP row on the same
        // number, which is exactly the confusion the column exists to remove.
        Assert.Contains(PortRegistryLimits.UdpProtocol, cut.Markup);
        Assert.Contains(PortRegistryLimits.TcpProtocol, cut.Markup);
    }

    [Fact]
    public void ObservedPortNobodyClaimed_StaysVisibleWithAConvertAction()
    {
        var scan = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var reservations = ThreeReservations();
        reservations.Add(new PortReservationDto
        {
            Id = 9,
            ServerId = 1,
            Port = 9000,
            OwnerLabel = "grafana",
            Source = PortReservationSource.Observed,
            DeclaredAt = scan,
            UpdatedAt = scan,
            ObservedAt = scan
        });

        var cut = RenderWith(reservations, lastScan: scan);

        Assert.Contains("grafana", cut.Markup);
        Assert.Contains("PortConvertToReservation", cut.Markup);
    }

    [Fact]
    public void ConvertAction_IsNotOfferedWithoutServerWrite()
    {
        var scan = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var reservations = ThreeReservations();
        reservations.Add(new PortReservationDto
        {
            Id = 9,
            ServerId = 1,
            Port = 9000,
            OwnerLabel = "grafana",
            Source = PortReservationSource.Observed,
            DeclaredAt = scan,
            UpdatedAt = scan,
            ObservedAt = scan
        });

        var cut = RenderWith(reservations, canWrite: false, lastScan: scan);

        Assert.Contains("grafana", cut.Markup);
        Assert.DoesNotContain("PortConvertToReservation", cut.Markup);
    }

    [Theory]
    [InlineData(false, true, "PortScanUnsupported")]
    [InlineData(null, true, "PortScanAgentUnknown")]
    [InlineData(true, false, "PortScanAgentOffline")]
    public void RefreshButton_SaysWhyItIsUnavailable(bool? capability, bool online, string expectedKey)
    {
        var cut = RenderWith(ThreeReservations(), observationAvailable: capability, agentOnline: online);

        Assert.Contains(expectedKey, cut.Markup);
        var button = cut.Find("button[title]");
        Assert.True(button.HasAttribute("disabled"));
    }

    [Fact]
    public void RefreshButton_IsEnabledOnACapableOnlineAgent()
    {
        var cut = RenderWith(ThreeReservations(), observationAvailable: true, agentOnline: true);

        var button = cut.Find("button[title]");
        Assert.False(button.HasAttribute("disabled"));
    }

    // Allocation and allocation windows.

    [Fact]
    public void AllocationWindow_ShowsWhetherItIsInheritedOrSetHere()
    {
        var cut = RenderWith(ThreeReservations());

        Assert.Contains("PortRangeDefault", cut.Markup);
        Assert.DoesNotContain("PortRangeExplicit", cut.Markup);
        Assert.Contains("PortAllocate", cut.Markup);
    }

    [Fact]
    public void AllocationAndWindow_AreNotOfferedWithoutServerWrite()
    {
        var cut = RenderWith(ThreeReservations(), canWrite: false);

        Assert.DoesNotContain("PortAllocate", cut.Markup);
        Assert.DoesNotContain("PortRangeFrom", cut.Markup);
    }

    [Fact]
    public void Allocate_WithoutAHolder_RefusesBeforeCallingTheApi()
    {
        var cut = RenderWith(ThreeReservations());

        // Allocate sits in the "More actions" menu beside its count (STD-BTN detection 11).
        cut.FindMenuItem("PortAllocate")!.Click();

        Assert.Contains("PortHolderRequired", cut.Markup);
        Assert.DoesNotContain(_handler!.Requests, request =>
            request.Url.EndsWith("/ports/allocate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Allocate_RefusedByTheWindow_ShowsHowManyPortsRemain()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(HttpMethod.Get, "/ports", new List<PortReservationDto>());
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto>());
        handler.SetJsonResponse(
            HttpMethod.Post, "/ports/allocate",
            new ApiError { Message = "The allocation window 10000-10001 has only 2 free port(s) left, and 5 were requested." },
            HttpStatusCode.Conflict);

        var api = Services.GetRequiredService<ApiClient>();
        var outcome = await api.ServerTools.AllocatePortsAsync(
            1, new AllocatePortsRequest { Count = 5, OwnerLabel = "portfolio" },
            Xunit.TestContext.Current.CancellationToken);

        Assert.Null(outcome.Value);
        Assert.Contains("only 2 free port(s)", outcome.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_RefusedByRegistry_ShowsTheHolderTheApiNamed()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(HttpMethod.Get, "/ports", new List<PortReservationDto>());
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto>());
        handler.SetJsonResponse(
            HttpMethod.Post, "/ports",
            new ApiError { Message = "Port 10031 is already registered to 'portfolio-prod-front' on this server." },
            HttpStatusCode.Conflict);

        var api = Services.GetRequiredService<ApiClient>();
        var outcome = await api.ServerTools.CreatePortReservationAsync(
            1, new CreatePortReservationRequest { Port = 10031, OwnerLabel = "aetheus" },
            Xunit.TestContext.Current.CancellationToken);

        Assert.Null(outcome.Value);
        Assert.Contains("portfolio-prod-front", outcome.Error!.Message);
    }

    [Fact]
    public void AnUnreadableRegistry_EmptiesTheGridAndSaysSo()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Get, "/ports", HttpStatusCode.InternalServerError);
        handler.SetJsonResponse(HttpMethod.Get, "/ports/range", new PortRangeDto
        {
            From = PortRegistryLimits.DefaultRangeFrom,
            To = PortRegistryLimits.DefaultRangeTo,
            IsExplicit = false
        });
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto>());

        var cut = Render<ServerPortsSection>(parameters => parameters
            .Add(c => c.ServerId, 1)
            .Add(c => c.AgentOnline, true));

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast =>
            toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger
            && (toast.Summary == "PortRegistryLoadFailed" || toast.Detail == "PortRegistryLoadFailed")));
        Assert.DoesNotContain("portfolio-prod-front", cut.Markup);
    }
}
