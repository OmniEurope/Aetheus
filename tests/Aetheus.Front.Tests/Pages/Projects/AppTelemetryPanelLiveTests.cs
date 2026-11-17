// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>An application added, edited or removed from the project header menu reaches the Telemetry panel through
/// the project's EntityChanged push, as it reaches the list above it: the panel no longer keeps saying "no application
/// supervised" until a full reload.</summary>
public sealed class AppTelemetryPanelLiveTests : BunitContext
{
    private const string AppsUrl = "api/appmonitoring/projects/19/apps";

    private readonly BunitTestHelper.TestHandler _handler;
    private CapturingHubConnectionFactory Hubs => (CapturingHubConnectionFactory)Services.GetRequiredService<HubConnectionFactory>();

    public AppTelemetryPanelLiveTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<HubConnectionFactory>(sp => new CapturingHubConnectionFactory(sp));
        _handler.SetJsonResponse(AppsUrl, new List<MonitoredAppDto>());
        // What the sub-tabs of a selected application read.
        _handler.SetJsonResponse("metrics/names", new List<string>());
        _handler.SetJsonResponse("/send-interval", new AppTelemetrySendIntervalDto { ServerIntervalSeconds = 300, Profile = DataRetentionProfile.Standard });
        _handler.SetJsonResponse("thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse("/logs", new PaginatedResult<AppLogEntryDto>());
        _handler.SetJsonResponse("/errors", new PaginatedResult<AppErrorEventDto>());
        _handler.SetJsonResponse("/web-analytics?days=30", new AppWebAnalyticsSummaryDto());
        _handler.SetJsonResponse("web-analytics/configuration", new AppWebAnalyticsConfigurationDto());
        _handler.SetJsonResponse(HttpMethod.Get, "/probe-now", new AppProbeNowDto());
        _handler.SetJsonResponse(HttpMethod.Get, "/telemetry-export-request", new AppTelemetryExportRequestDto());
    }

    private static MonitoredAppDto App(int id, string name) => new() { Id = id, ProjectId = 19, Name = name, Enabled = true };

    private async Task ProjectChangedAsync(int projectId = 19)
    {
        await Hubs.WhenCreatedAsync("entities");
        // The sub-tabs open their own entities connections (other handlers); the panel's is the one that handles
        // EntityChanged, registered a moment after the connection is built.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            foreach (var (path, connection) in Hubs.Created.Where(created => created.Path == "entities"))
            {
                try
                {
                    await HubHandlerInvoker.InvokeAsync(connection, "EntityChanged", ResourceType.Project, projectId, "Created");
                    return;
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("No handler registered", StringComparison.Ordinal))
                {
                    // Not the panel's connection.
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "No entities connection registered an EntityChanged handler.");
            await Task.Delay(25, Xunit.TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AnAppAddedAfterThePanelOpened_ReplacesTheEmptyState_WithoutAReload()
    {
        var cut = Render<AppTelemetryPanel>(p => p.Add(x => x.ProjectId, 19));
        cut.WaitForAssertion(() => Assert.Contains("NoMonitoredApps", cut.Markup, StringComparison.Ordinal));

        _handler.SetJsonResponse(AppsUrl, new List<MonitoredAppDto> { App(5, "w1") });
        await ProjectChangedAsync();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='telemetry-app-field']")), TimeSpan.FromSeconds(30));
        Assert.DoesNotContain("NoMonitoredApps", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppRemovedAfterThePanelOpened_ReturnsToTheEmptyState()
    {
        _handler.SetJsonResponse(AppsUrl, new List<MonitoredAppDto> { App(5, "w1") });
        var cut = Render<AppTelemetryPanel>(p => p.Add(x => x.ProjectId, 19));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='telemetry-app-field']")));

        _handler.SetJsonResponse(AppsUrl, new List<MonitoredAppDto>());
        await ProjectChangedAsync();

        cut.WaitForAssertion(() => Assert.Contains("NoMonitoredApps", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task APushOfAnotherProject_DoesNotReadTheListAgain()
    {
        var cut = Render<AppTelemetryPanel>(p => p.Add(x => x.ProjectId, 19));
        cut.WaitForAssertion(() => Assert.Contains("NoMonitoredApps", cut.Markup, StringComparison.Ordinal));
        var before = _handler.RequestsSnapshot().Count(r => r.Url.Contains(AppsUrl, StringComparison.Ordinal));

        await ProjectChangedAsync(projectId: 20);
        await Task.Delay(800, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(before, _handler.RequestsSnapshot().Count(r => r.Url.Contains(AppsUrl, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AFailedReadAfterAPush_KeepsTheListOnScreen()
    {
        _handler.SetJsonResponse(AppsUrl, new List<MonitoredAppDto> { App(5, "w1") });
        var cut = Render<AppTelemetryPanel>(p => p.Add(x => x.ProjectId, 19));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='telemetry-app-field']")));
        var before = _handler.RequestsSnapshot().Count(r => r.Url.Contains(AppsUrl, StringComparison.Ordinal));

        _handler.SetResponse(HttpMethod.Get, AppsUrl, HttpStatusCode.InternalServerError);
        await ProjectChangedAsync();

        cut.WaitForAssertion(() => Assert.True(_handler.RequestsSnapshot().Count(r => r.Url.Contains(AppsUrl, StringComparison.Ordinal)) > before), TimeSpan.FromSeconds(30));
        Assert.NotEmpty(cut.FindAll("[data-testid='telemetry-app-field']"));
        Assert.DoesNotContain("NoMonitoredApps", cut.Markup, StringComparison.Ordinal);
    }
}
