// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects;
using Bunit;

namespace Aetheus.Front.Tests.Projects;

/// <summary> R-29: the telemetry test shows its waiting, received and nothing-received states from the state the server
/// reports, each from data that really arrived after the request (the test localizer echoes keys; the French sentences are
/// checked in <see cref="AppImmediateActionTextTests"/>). </summary>
public sealed class AppTelemetryTestStatesTests : BunitContext
{
    private static readonly DateTime Requested = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);
    private readonly BunitTestHelper.TestHandler _handler;

    public AppTelemetryTestStatesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/probe-now", new AppProbeNowDto());
    }

    private IRenderedComponent<AppImmediateActions> RenderWith(AppTelemetryExportRequestDto state)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "/telemetry-export-request", state);
        return Render<AppImmediateActions>(p => p.Add(c => c.App,
            new MonitoredAppDto { Id = 5, ProjectId = 1, Name = "w1", Enabled = true, ProbeUrl = "https://w1.example/h", HasIngestKey = true }));
    }

    private static string Result(IRenderedComponent<AppImmediateActions> cut) =>
        cut.Find("[data-testid=app-telemetry-now-result]").TextContent;

    [Fact]
    public void AWaitingTest_SaysItWaits_AndHasNotConcluded()
    {
        var cut = RenderWith(new AppTelemetryExportRequestDto { RequestedAt = Requested });

        var line = Result(cut);
        Assert.Contains("AppTelemetryNowPending", line, StringComparison.Ordinal);
        Assert.DoesNotContain("AppTelemetryNowReceived", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestAnsweredByTheExport_ShowsItReceived()
    {
        var cut = RenderWith(new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            DeliveredAt = Requested.AddSeconds(12),
            ReceivedAt = Requested.AddSeconds(13),
            LastIngestAt = Requested.AddSeconds(13),
            DataReceivedAt = Requested.AddSeconds(13)
        });

        Assert.Contains("AppTelemetryNowReceived", Result(cut), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingReceived_SaysSoWithTheLastDataBeforeTheTest()
    {
        var cut = RenderWith(new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            TimedOut = true,
            LastIngestAt = Requested.AddHours(-2)
        });

        var line = Result(cut);
        Assert.Contains("AppTelemetryNowNotTaken", line, StringComparison.Ordinal);
        Assert.Contains("AppTelemetryNowDataBefore", line, StringComparison.Ordinal);
        Assert.DoesNotContain("AppTelemetryNowDataButNotTaken", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderPackageWhosePeriodicExportArrived_IsNotReportedAsNothingReceived()
    {
        var cut = RenderWith(new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            TimedOut = true,
            LastIngestAt = Requested.AddSeconds(70),
            DataReceivedAt = Requested.AddSeconds(70)
        });

        var line = Result(cut);
        Assert.Contains("AppTelemetryNowDataButNotTaken", line, StringComparison.Ordinal);
        Assert.DoesNotContain("AppTelemetryNowNotTaken", line, StringComparison.Ordinal);
    }
}
