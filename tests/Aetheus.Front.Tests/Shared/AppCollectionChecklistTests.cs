// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-26: the checklist of what the setup prepared and what still lacks, every line read from a real field.</summary>
public sealed class AppCollectionChecklistTests : BunitContext
{
    private const string ConfigurationUrl = "api/appmonitoring/apps/5/web-analytics/configuration";
    private const string SummaryUrl = "api/appmonitoring/apps/5/web-analytics?days=30";

    private static readonly DateTime Received = new(2026, 11, 5, 10, 38, 0, DateTimeKind.Utc);

    private static MonitoredAppDto App(bool enabled = true, bool hasKey = false, DateTime? lastIngest = null, string? probeUrl = null) => new()
    {
        Id = 5,
        ProjectId = 19,
        Name = "w1",
        Enabled = enabled,
        HasIngestKey = hasKey,
        IngestKeyCreatedAt = hasKey ? Received.AddHours(-1) : null,
        LastIngestAt = lastIngest,
        ProbeUrl = probeUrl
    };

    private BunitTestHelper.TestHandler Arrange(AppWebAnalyticsConfigurationDto? configuration, DateTime? audienceReceived)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(ConfigurationUrl, configuration ?? new AppWebAnalyticsConfigurationDto());
        handler.SetJsonResponse(SummaryUrl, new AppWebAnalyticsSummaryDto { LastIngestAtUtc = audienceReceived });
        return handler;
    }

    private IRenderedComponent<AppCollectionChecklist> RenderChecklist(MonitoredAppDto app, int version = 0) =>
        Render<AppCollectionChecklist>(parameters => parameters.Add(c => c.App, app).Add(c => c.Version, version));

    private static (string Status, string Text)[] Lines(IRenderedComponent<AppCollectionChecklist> cut) =>
        [.. cut.FindAll("[data-testid='collection-line']").Select(line => (line.GetAttribute("data-status")!, line.TextContent))];

    [Fact]
    public void NothingPrepared_NamesEachMissingPrerequisite_FromTheRealFields()
    {
        // The observed w1 case: a key was configured and the app enabled, yet the audience is off, the public
        // ingestion closed, the site id and origins empty, no pseudonymization key, nothing received, no probe URL.
        Arrange(new AppWebAnalyticsConfigurationDto(), audienceReceived: null);

        var cut = RenderChecklist(App(hasKey: true));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='collection-checklist']")));
        var lines = Lines(cut);
        string[] missing =
        [
            "CollectionTechnicalNone", "CollectionAudienceDisabled", "CollectionSiteIdMissing", "CollectionOriginsMissing",
            "CollectionPublicIngestMissing", "CollectionPseudonymKeyMissing", "CollectionAudienceNone"
        ];
        foreach (var key in missing)
            Assert.Contains(lines, line => line.Status == "Missing" && line.Text.Contains(key, StringComparison.Ordinal));
        // What the setup did prepare is ready, and a key alone does not stand for a working collection.
        Assert.Contains(lines, line => line.Status == "Ready" && line.Text.Contains("CollectionIngestKeyReady", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Status == "Ready" && line.Text.Contains("CollectionAppEnabled", StringComparison.Ordinal));
        // No probe URL is told, but it is not a prerequisite of any telemetry signal: info, not missing.
        Assert.Contains(lines, line => line.Status == "Info" && line.Text.Contains("CollectionProbeNone", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Status == "Missing" && line.Text.Contains("CollectionProbe", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledAppWithoutKey_SaysSo()
    {
        Arrange(new AppWebAnalyticsConfigurationDto(), audienceReceived: null);

        var cut = RenderChecklist(App(enabled: false, hasKey: false));

        cut.WaitForAssertion(() => Assert.NotEmpty(Lines(cut)));
        var lines = Lines(cut);
        Assert.Contains(lines, line => line.Status == "Missing" && line.Text.Contains("CollectionAppDisabled", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Status == "Missing" && line.Text.Contains("CollectionIngestKeyMissing", StringComparison.Ordinal));
    }

    [Fact]
    public void EverythingConfiguredAndReceived_ShowsNoChecklist()
    {
        var handler = Arrange(new AppWebAnalyticsConfigurationDto
        {
            Enabled = true,
            PublicIngestEnabled = true,
            SiteId = "w1",
            AllowedOrigins = ["https://w1.sonytumen.com"],
            PseudonymKeyVersion = 1,
            PseudonymKeyCreatedAt = Received.AddHours(-1)
        }, audienceReceived: Received);

        var cut = RenderChecklist(App(hasKey: true, lastIngest: Received, probeUrl: "https://w1.sonytumen.com/health"));

        cut.WaitForAssertion(() => Assert.Contains(handler.Requests, request => request.Url.Contains(SummaryUrl, StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='collection-checklist']")));
    }

    [Fact]
    public void AConfigurationThatCannotBeRead_IsSaidUnreadable_NotDisabled()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Get, ConfigurationUrl, HttpStatusCode.InternalServerError);
        handler.SetJsonResponse(SummaryUrl, new AppWebAnalyticsSummaryDto());

        var cut = RenderChecklist(App(hasKey: true, lastIngest: Received));

        cut.WaitForAssertion(() => Assert.Contains(Lines(cut), line => line.Text.Contains("CollectionAudienceUnreadable", StringComparison.Ordinal)));
        Assert.DoesNotContain(Lines(cut), line => line.Text.Contains("CollectionAudienceDisabled", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(cut), line => line.Text.Contains("CollectionSiteIdMissing", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangeOfTheSettings_ReadsTheLinesAgain()
    {
        var handler = Arrange(new AppWebAnalyticsConfigurationDto(), audienceReceived: null);
        var cut = RenderChecklist(App(hasKey: true, lastIngest: Received));
        cut.WaitForAssertion(() => Assert.Contains(Lines(cut), line => line.Text.Contains("CollectionAudienceDisabled", StringComparison.Ordinal)));

        handler.SetJsonResponse(ConfigurationUrl, new AppWebAnalyticsConfigurationDto
        {
            Enabled = true,
            PublicIngestEnabled = true,
            SiteId = "w1",
            AllowedOrigins = ["https://w1.sonytumen.com"],
            PseudonymKeyVersion = 1,
            PseudonymKeyCreatedAt = Received
        });
        cut.Render(parameters => parameters.Add(c => c.App, App(hasKey: true, lastIngest: Received)).Add(c => c.Version, 1));

        cut.WaitForAssertion(() => Assert.Contains(Lines(cut), line => line.Status == "Ready" && line.Text.Contains("CollectionAudienceEnabled", StringComparison.Ordinal)));
        Assert.DoesNotContain(Lines(cut), line => line.Text.Contains("CollectionSiteIdMissing", StringComparison.Ordinal));
    }
}
