// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Projects;

/// <summary> R-29: one visible action (the probe), the telemetry request in its menu, the result shown with its time,
/// and the missing prerequisites and refusals said instead of a silent nothing. </summary>
public sealed class AppImmediateActionsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AppImmediateActionsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/probe-now", new AppProbeNowDto());
        _handler.SetJsonResponse(HttpMethod.Get, "/telemetry-export-request", new AppTelemetryExportRequestDto());
    }

    private static MonitoredAppDto App(bool enabled = true, string? url = "https://w1.example/health", bool key = true) =>
        new() { Id = 5, ProjectId = 1, Name = "w1", Enabled = enabled, ProbeUrl = url, HasIngestKey = key };

    [Fact]
    public void TheProbeIsTheVisibleAction_AndItsResultIsShownWithItsTime()
    {
        var at = new DateTime(2026, 11, 6, 9, 0, 1, DateTimeKind.Utc);
        _handler.SetJsonResponse(HttpMethod.Post, "apps/5/probe-now", new AppProbeNowDto
        {
            RequestedAt = at,
            Result = new AppHealthSampleDto { Timestamp = at, IsUp = true, StatusCode = 200, ResponseTimeMs = 31 }
        });
        var cut = Render<AppImmediateActions>(p => p.Add(c => c.App, App()));

        cut.Find("[data-testid=app-probe-now] button").Click();

        // The test localizer echoes keys; the sentence with its time is checked against the real resources in
        // AppImmediateActionTextTests.
        cut.WaitForAssertion(() => Assert.Contains("AppProbeNowUp", cut.Find("[data-testid=app-probe-now-result]").TextContent));
        Assert.Contains(_handler.RequestsSnapshot(), request => request.Method == "POST" && request.Url.Contains("apps/5/probe-now"));
    }

    [Fact]
    public void AForbiddenTarget_IsRenderedAsItsReason_NotTheStoredCodeOrAnExceptionText()
    {
        var at = new DateTime(2026, 11, 6, 9, 0, 1, DateTimeKind.Utc);
        _handler.SetJsonResponse(HttpMethod.Post, "apps/5/probe-now", new AppProbeNowDto
        {
            RequestedAt = at,
            Result = new AppHealthSampleDto { Timestamp = at, IsUp = false, Error = "probe:forbidden_target;host=localhost:5301" }
        });
        // The real French resources, so the rendered sentence itself is checked (the default localizer echoes keys).
        Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(
            Aetheus.Front.Tests.TestDoubles.ResxLocalizer.French());
        var cut = Render<AppImmediateActions>(p => p.Add(c => c.App, App()));

        cut.Find("[data-testid=app-probe-now] button").Click();

        cut.WaitForAssertion(() =>
        {
            var line = cut.Find("[data-testid=app-probe-now-result]").TextContent;
            Assert.Contains("Aetheus refuse de sonder une adresse privée ou locale (localhost:5301), par sécurité", line, StringComparison.Ordinal);
            Assert.DoesNotContain("probe:", line, StringComparison.Ordinal);
            Assert.DoesNotContain("forbidden (private", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ARefusal_IsSaid()
    {
        _handler.SetResponse("apps/5/telemetry-export-request", HttpStatusCode.Forbidden);
        _handler.SetResponse("apps/5/probe-now", HttpStatusCode.Forbidden);
        var cut = Render<AppImmediateActions>(p => p.Add(c => c.App, App()));

        cut.Find("[data-testid=app-probe-now] button").Click();

        cut.WaitForAssertion(() => Assert.Contains("AppImmediateForbidden", cut.Find("[data-testid=app-immediate-refusal]").TextContent));
    }

    [Fact]
    public void MissingPrerequisites_AreSaid_AndTheActionsAreDisabled()
    {
        var cut = Render<AppImmediateActions>(p => p.Add(c => c.App, App(url: null, key: false)));

        Assert.Contains("AppImmediateNoProbeUrl", cut.Find("[data-testid=app-immediate-prerequisite]").TextContent);
        Assert.True(cut.Find("[data-testid=app-probe-now] button").HasAttribute("disabled"));
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == "POST");
    }
}
