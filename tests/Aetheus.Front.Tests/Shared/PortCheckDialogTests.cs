// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Port verdicts combine registry ownership and observed listeners; an unregistered
/// external listener is a warning rather than a free port.</summary>
public class PortCheckDialogTests : BunitContext
{
    private static readonly DateTime Scan = new(2026, 9, 6, 9, 30, 0, DateTimeKind.Utc);

    private BunitTestHelper.TestHandler? _handler;

    private IRenderedComponent<PortCheckDialog> RenderWith(PortCheckResultDto result)
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/check", result);
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/observe", new ServerTaskDto { Id = 5 });

        var cut = Render<PortCheckDialog>(parameters => parameters.Add(c => c.ServerId, 1));
        cut.Find("input#port-check-ports").Input("10041");
        cut.FindAll("button").First(button => button.TextContent.Contains("CheckPorts", StringComparison.Ordinal)).Click();
        return cut;
    }

    private static PortCheckResultDto Result(
        PortCheckEntryDto entry, DateTime? lastScan = null, bool observationAvailable = false) => new()
        {
            ServerId = 1,
            ServerName = "vps2577917",
            Entries = [entry],
            LastScanAt = lastScan,
            ObservationAvailable = observationAvailable
        };

    [Fact]
    public void FreeAndNotListening_ReadsFree()
    {
        var cut = RenderWith(Result(
            new PortCheckEntryDto { Port = 10041, IsFree = true, Observation = PortObservationState.NotListening },
            lastScan: Scan));

        Assert.Contains("PortVerdictFree", cut.Markup);
        // Nothing claims it and nothing answers on it, so the detail line has nothing to add.
        Assert.DoesNotContain("PortListeningNo", cut.Markup);
    }

    [Fact]
    public void FreeButListening_ReadsListeningUndeclaredAndNamesTheHolder()
    {
        var cut = RenderWith(Result(
            new PortCheckEntryDto
            {
                Port = 10041,
                IsFree = true,
                Observation = PortObservationState.Listening,
                ObservedHolder = "grafana"
            },
            lastScan: Scan));

        // Nobody declared it, yet something answers: amber, and the holder is named.
        Assert.Contains("PortVerdictListeningUndeclared", cut.Markup);
        Assert.Contains("grafana", cut.Markup);
    }

    [Fact]
    public void TakenAndNotListening_NamesWhoHoldsIt()
    {
        var cut = RenderWith(Result(
            new PortCheckEntryDto
            {
                Port = 10031,
                IsFree = false,
                OwnerLabel = "portfolio-prod-front",
                Source = PortReservationSource.Declared,
                Observation = PortObservationState.NotListening
            },
            lastScan: Scan));

        Assert.Contains("PortVerdictTakenBy", cut.Markup);
        Assert.Contains("portfolio-prod-front", cut.Markup);
    }

    [Fact]
    public void NeverScanned_SaysSoInsteadOfShowingAScanDate()
    {
        var cut = RenderWith(Result(
            new PortCheckEntryDto { Port = 10041, IsFree = true, Observation = PortObservationState.NeverScanned }));

        Assert.Contains("PortNeverScanned", cut.Markup);
        Assert.Contains("PortListeningUnknown", cut.Markup);
    }

    [Fact]
    public void ScannedHost_ShowsWhenTheScanRan()
    {
        var cut = RenderWith(Result(
            new PortCheckEntryDto { Port = 10041, IsFree = true, Observation = PortObservationState.NotListening },
            lastScan: Scan));

        Assert.Contains("PortLastScan", cut.Markup);
        Assert.DoesNotContain("PortNeverScanned", cut.Markup);
    }

    [Fact]
    public void RefreshLink_IsOfferedOnlyWhenTheAgentCanScan()
    {
        var withoutAgent = RenderWith(Result(
            new PortCheckEntryDto { Port = 10041, IsFree = true, Observation = PortObservationState.NeverScanned }));

        Assert.DoesNotContain("PortScanRefresh", withoutAgent.Markup);
    }

    [Fact]
    public void RefreshLink_QueuesAScanAndRunsTheCheckAgain()
    {
        var cut = RenderWith(Result(
            new PortCheckEntryDto { Port = 10041, IsFree = true, Observation = PortObservationState.NotListening },
            lastScan: Scan,
            observationAvailable: true));

        cut.FindAll("button").First(button => button.TextContent.Contains("PortScanRefresh", StringComparison.Ordinal)).Click();

        Assert.Contains(_handler!.Requests, request =>
            request.Method == "POST" && request.Url.EndsWith("/ports/observe", StringComparison.Ordinal));
        // Two checks: the first one, then the one the refresh triggers.
        Assert.Equal(2, _handler.Requests.Count(request =>
            request.Method == "POST" && request.Url.EndsWith("/ports/check", StringComparison.Ordinal)));
    }
}
