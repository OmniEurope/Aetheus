// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-26: what the wizard prepares for the monitored application, told item by item. A key is reported created only when
/// the service created it, a second preparation keeps what exists, and a failure is its own reason, never a claimed success.</summary>
public sealed class PipelineSetupCollectionServiceTests
{
    private const int ProjectId = 4;
    private const int AppId = 11;

    private readonly IAppMonitoringService _monitoring = Substitute.For<IAppMonitoringService>();
    private readonly IAppMonitoringRepository _apps = Substitute.For<IAppMonitoringRepository>();
    private readonly IAppTelemetryService _telemetry = Substitute.For<IAppTelemetryService>();
    private readonly IAppWebAnalyticsConfigurationService _analytics = Substitute.For<IAppWebAnalyticsConfigurationService>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PipelineSetupCollectionService Create()
    {
        _apps.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Atlas");
        _monitoring.EnsureCollectionAppAsync(ProjectId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new EnsuredCollectionApp(AppId, Created: true));
        _libraries.ResolveLibrariesAsync(Arg.Any<List<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["DOMAIN"] = "atlas.example.com" });
        return new PipelineSetupCollectionService(_monitoring, _apps, _telemetry, _analytics, _libraries, NullLogger<PipelineSetupCollectionService>.Instance);
    }

    private static PipelineSetupCollectionRequest Both() => new() { ProjectId = ProjectId, Telemetry = true, Audience = true };

    private static AppWebAnalyticsConfigurationDto Configured(string siteId = "atlas", params string[] origins) => new()
    {
        Enabled = true,
        PublicIngestEnabled = true,
        SiteId = siteId,
        AllowedOrigins = origins,
        StorageBudgetBytes = 1000,
        PseudonymKeyVersion = 1
    };

    private static PipelineSetupCollectionOutcome Outcome(PipelineSetupCollectionResultDto result, PipelineSetupCollectionKind kind) =>
        result.Items.Single(item => item.Kind == kind).Outcome;

    private void NoKeyYet() =>
        _apps.GetAppAsync(AppId, Arg.Any<CancellationToken>()).Returns(new MonitoredApp { Id = AppId, ProjectId = ProjectId });

    [Fact]
    public async Task Prepare_CreatesEverythingMissing_AndSaysDoneForEachItem()
    {
        var service = Create();
        NoKeyYet();
        _telemetry.GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>()).Returns(new IngestKeyResponse { Key = "secret" });
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(new AppWebAnalyticsConfigurationDto { StorageBudgetBytes = 1000 });
        _analytics.ConfigureAsync(AppId, Arg.Any<ConfigureAppWebAnalyticsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Configured("atlas", "https://atlas.example.com"));

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        Assert.Equal(7, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(PipelineSetupCollectionOutcome.Done, item.Outcome));
        Assert.False(result.HasFailure);
        Assert.Equal("https://atlas.example.com", result.Items.Single(item => item.Kind == PipelineSetupCollectionKind.Origins).Detail);
        await _telemetry.Received(1).GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>());
        await _analytics.Received(1).ConfigureAsync(AppId, Arg.Is<ConfigureAppWebAnalyticsRequest>(request =>
            request.Enabled && request.PublicIngestEnabled && request.SiteId == "atlas" && request.StorageBudgetBytes == 1000
            && request.AllowedOrigins.SequenceEqual(new[] { "https://atlas.example.com" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prepare_TheIngestionKeyText_NeverLeavesTheService()
    {
        var service = Create();
        NoKeyYet();
        _telemetry.GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>()).Returns(new IngestKeyResponse { Key = "plain-secret-key" });

        var result = await service.PrepareAsync(new PipelineSetupCollectionRequest { ProjectId = ProjectId, Telemetry = true }, Ct);

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Items, item => item.Detail is not null && item.Detail.Contains("plain-secret-key", StringComparison.Ordinal));
        Assert.Equal([PipelineSetupCollectionKind.AppEnabled, PipelineSetupCollectionKind.IngestKey], result.Items.Select(item => item.Kind).ToArray());
    }

    [Fact]
    public async Task Prepare_SecondTime_KeepsWhatExists_NoKeyIsRotated_AndTheSiteAndOriginsAreKept()
    {
        var service = Create();
        _apps.GetFirstEnabledAppForUpdateAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(new MonitoredApp { Id = AppId, ProjectId = ProjectId });
        _monitoring.EnsureCollectionAppAsync(ProjectId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new EnsuredCollectionApp(AppId, Created: false));
        _apps.GetAppAsync(AppId, Arg.Any<CancellationToken>()).Returns(new MonitoredApp { Id = AppId, ProjectId = ProjectId, IngestKeyHash = "hash" });
        var existing = Configured("custom-site", "https://atlas.example.com");
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(existing);
        _analytics.ConfigureAsync(AppId, Arg.Any<ConfigureAppWebAnalyticsRequest>(), Arg.Any<CancellationToken>()).Returns(existing);

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        Assert.Equal(7, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(PipelineSetupCollectionOutcome.AlreadyPresent, item.Outcome));
        await _telemetry.DidNotReceiveWithAnyArgs().GenerateIngestKeyAsync(default, Ct);
        await _analytics.DidNotReceiveWithAnyArgs().RotateKeyAsync(default, Ct);
        await _analytics.Received(1).ConfigureAsync(AppId, Arg.Is<ConfigureAppWebAnalyticsRequest>(request => request.SiteId == "custom-site"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prepare_AnExistingConfiguration_GetsTheProductionOriginAdded_WithoutLosingItsOwn()
    {
        var service = Create();
        _apps.GetFirstEnabledAppForUpdateAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(new MonitoredApp { Id = AppId });
        _apps.GetAppAsync(AppId, Arg.Any<CancellationToken>()).Returns(new MonitoredApp { Id = AppId, IngestKeyHash = "hash" });
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(Configured("atlas", "https://other.example.org"));
        _analytics.ConfigureAsync(AppId, Arg.Any<ConfigureAppWebAnalyticsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Configured("atlas", "https://other.example.org", "https://atlas.example.com"));

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        Assert.Equal(PipelineSetupCollectionOutcome.Done, Outcome(result, PipelineSetupCollectionKind.Origins));
        Assert.Equal(PipelineSetupCollectionOutcome.AlreadyPresent, Outcome(result, PipelineSetupCollectionKind.SiteId));
        await _analytics.Received(1).ConfigureAsync(AppId, Arg.Is<ConfigureAppWebAnalyticsRequest>(request =>
            request.AllowedOrigins.SequenceEqual(new[] { "https://other.example.org", "https://atlas.example.com" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prepare_WithoutAProductionAddress_ConfiguresNoAudience_AndFailsItsFiveItemsWithTheReason()
    {
        var service = Create();
        NoKeyYet();
        _telemetry.GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>()).Returns(new IngestKeyResponse { Key = "k" });
        _libraries.ResolveLibrariesAsync(Arg.Any<List<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(new AppWebAnalyticsConfigurationDto());

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        Assert.True(result.HasFailure);
        Assert.Equal(PipelineSetupCollectionOutcome.Done, Outcome(result, PipelineSetupCollectionKind.IngestKey));
        var failed = result.Items.Where(item => item.Outcome == PipelineSetupCollectionOutcome.Failed).ToList();
        Assert.Equal(5, failed.Count);
        Assert.All(failed, item => Assert.Contains("production address", item.Detail, StringComparison.Ordinal));
        await _analytics.DidNotReceiveWithAnyArgs().ConfigureAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Prepare_ASavedConfigurationWithoutPseudonymKey_IsReportedFailed_NeverDone()
    {
        var service = Create();
        NoKeyYet();
        _telemetry.GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>()).Returns(new IngestKeyResponse { Key = "k" });
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(new AppWebAnalyticsConfigurationDto());
        _analytics.ConfigureAsync(AppId, Arg.Any<ConfigureAppWebAnalyticsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Configured("atlas", "https://atlas.example.com") with { PseudonymKeyVersion = 0 });

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        Assert.Equal(PipelineSetupCollectionOutcome.Failed, Outcome(result, PipelineSetupCollectionKind.PseudonymKey));
        Assert.Equal(PipelineSetupCollectionOutcome.Done, Outcome(result, PipelineSetupCollectionKind.SiteId));
    }

    [Fact]
    public async Task Prepare_AnotherApplicationHoldingTheSiteId_GetsTheApplicationIdAppended()
    {
        var service = Create();
        NoKeyYet();
        _telemetry.GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>()).Returns(new IngestKeyResponse { Key = "k" });
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(new AppWebAnalyticsConfigurationDto());
        _apps.GetAppByAnalyticsSiteIdAsync("atlas", Arg.Any<CancellationToken>()).Returns(new MonitoredApp { Id = 999 });
        _analytics.ConfigureAsync(AppId, Arg.Any<ConfigureAppWebAnalyticsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Configured("atlas-11", "https://atlas.example.com"));

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        Assert.Equal("atlas-11", result.Items.Single(item => item.Kind == PipelineSetupCollectionKind.SiteId).Detail);
        await _analytics.Received(1).ConfigureAsync(AppId, Arg.Is<ConfigureAppWebAnalyticsRequest>(request => request.SiteId == "atlas-11"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prepare_AValidationMessageOfTheService_IsShownAsWritten()
    {
        var service = Create();
        NoKeyYet();
        _telemetry.GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>()).Returns(new IngestKeyResponse { Key = "k" });
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(new AppWebAnalyticsConfigurationDto());
        _analytics.ConfigureAsync(AppId, Arg.Any<ConfigureAppWebAnalyticsRequest>(), Arg.Any<CancellationToken>())
            .Returns<AppWebAnalyticsConfigurationDto?>(_ => throw new InvalidOperationException("The vault of the project is missing."));

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        Assert.All(result.Items.Where(item => item.Outcome == PipelineSetupCollectionOutcome.Failed),
            item => Assert.Equal("The vault of the project is missing.", item.Detail));
        Assert.Equal(5, result.Items.Count(item => item.Outcome == PipelineSetupCollectionOutcome.Failed));
    }

    [Fact]
    public async Task Prepare_ADatabaseFailure_SaysItFailed_WithoutItsInternalDetails()
    {
        var service = Create();
        NoKeyYet();
        _telemetry.GenerateIngestKeyAsync(AppId, Arg.Any<CancellationToken>())
            .Returns<IngestKeyResponse?>(_ => throw new DbUpdateException("duplicate key value violates unique constraint ix_secret_table"));
        _analytics.GetAsync(AppId, Arg.Any<CancellationToken>()).Returns(Configured("atlas", "https://atlas.example.com"));
        _analytics.ConfigureAsync(AppId, Arg.Any<ConfigureAppWebAnalyticsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Configured("atlas", "https://atlas.example.com"));

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        var key = result.Items.Single(item => item.Kind == PipelineSetupCollectionKind.IngestKey);
        Assert.Equal(PipelineSetupCollectionOutcome.Failed, key.Outcome);
        Assert.DoesNotContain("ix_secret_table", key.Detail, StringComparison.Ordinal);
        // The failure of one item does not stop the others, and the audience is still prepared.
        Assert.Equal(PipelineSetupCollectionOutcome.AlreadyPresent, Outcome(result, PipelineSetupCollectionKind.Origins));
    }

    [Fact]
    public async Task Prepare_TheApplicationThatCannotBeEnabled_StopsThere_WithTheReason()
    {
        var service = Create();
        _monitoring.EnsureCollectionAppAsync(ProjectId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<EnsuredCollectionApp?>(_ => throw new BadRequestException("Too many applications."));

        var result = await service.PrepareAsync(Both(), Ct);

        Assert.NotNull(result);
        var only = Assert.Single(result.Items);
        Assert.Equal(PipelineSetupCollectionKind.AppEnabled, only.Kind);
        Assert.Equal(PipelineSetupCollectionOutcome.Failed, only.Outcome);
        Assert.Equal("Too many applications.", only.Detail);
        await _telemetry.DidNotReceiveWithAnyArgs().GenerateIngestKeyAsync(default, Ct);
    }

    [Fact]
    public async Task Prepare_AMissingProject_GivesNull()
    {
        var service = Create();
        _apps.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns((string?)null);

        Assert.Null(await service.PrepareAsync(Both(), Ct));
    }

    [Fact]
    public async Task Prepare_NothingRequested_TouchesNothing()
    {
        var service = Create();

        var result = await service.PrepareAsync(new PipelineSetupCollectionRequest { ProjectId = ProjectId }, Ct);

        Assert.NotNull(result);
        Assert.Empty(result.Items);
        await _monitoring.DidNotReceiveWithAnyArgs().EnsureCollectionAppAsync(default, default, Ct);
    }
}
