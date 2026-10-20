// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

public class MonitoredAppsListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public MonitoredAppsListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SeedApps()
    {
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps", new List<MonitoredAppDto>
        {
            new() { Id = 1, ProjectId = 1, Name = "toto-qa", CurrentStatus = AppHealthStatus.Down, ProbeUrl = "https://qa.example.com", Uptime24h = 0.5 }
        });
        // S-UX-SPKL: the list now fetches 24 h availability samples per app for the inline sparkline.
        _handler.SetJsonResponse("api/appmonitoring/apps/1/samples", new List<AppHealthSampleDto>
        {
            new() { Timestamp = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc), IsUp = true },
            new() { Timestamp = new DateTime(2026, 1, 10, 11, 0, 0, DateTimeKind.Utc), IsUp = false }
        });
    }

    [Fact]
    public void RendersApps_AndShowsAddButton_WhenCanWrite()
    {
        SeedApps();
        var cut = Render<MonitoredAppsList>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("toto-qa"));

        Assert.Contains("toto-qa", cut.Markup);
        Assert.Contains("AppHealthDown", cut.Markup);   // status badge (stub localizer echoes the key)
        Assert.Contains("AddMonitoredApp", cut.Markup);  // create button visible with write permission
    }

    [Fact]
    public void HidesAddButton_WhenReadOnly()
    {
        // Replace the read+write permission service with a read-only one (last DI registration wins).
        var readOnly = new PermissionService();
        readOnly.SetPermissions(
            [new EffectivePermissionDto { ResourceType = ResourceType.Project, ResourceId = null, Permission = Permission.Read }],
            isAdmin: false);
        Services.AddSingleton(readOnly);

        SeedApps();
        var cut = Render<MonitoredAppsList>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("toto-qa"));

        Assert.DoesNotContain("AddMonitoredApp", cut.Markup);
    }

    [Fact]
    public void UnknownStatus_AfterAProbe_DoesNotClaimItWasNeverProbed()
    {
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps", new List<MonitoredAppDto>
        {
            new()
            {
                Id = 1,
                ProjectId = 1,
                Name = "portfolio",
                CurrentStatus = AppHealthStatus.Unknown,
                LastCheckedAt = new DateTime(2026, 7, 23, 20, 0, 0, DateTimeKind.Utc)
            }
        });
        _handler.SetJsonResponse("api/appmonitoring/apps/1/samples", new List<AppHealthSampleDto>());

        var cut = Render<MonitoredAppsList>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("portfolio"));

        Assert.Contains("AppHealthUnknown", cut.Markup);
        Assert.DoesNotContain("AppHealthNeverProbed", cut.Markup);
    }
}
