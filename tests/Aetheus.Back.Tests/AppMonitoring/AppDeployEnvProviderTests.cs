// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.AppMonitoring.Ingest;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

public class AppDeployEnvProviderTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ISecretMaskingService _masking = Substitute.For<ISecretMaskingService>();
    private readonly IIngestService _ingest = Substitute.For<IIngestService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly IngestKeyHasher _hasher =
        new(Options.Create(new JwtOptions { SigningKey = "unit-test-signing-key-least-32-bytes!!" }));

    public AppDeployEnvProviderTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        _db.MonitoredApps.Add(new MonitoredApp { Id = 1, ProjectId = 1, Name = "app", Enabled = true });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static readonly DateTimeOffset T0 = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private AppDeployEnvProvider Build(string? baseUrl, DateTimeOffset? now = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppMonitoring:IngestBaseUrl"] = baseUrl
        }).Build();
        return new AppDeployEnvProvider(
            new AppMonitoringRepository(_db),
            _hasher,
            _masking,
            _ingest,
            _audit,
            _vaults,
            config,
            new FakeTimeProvider(now ?? T0));
    }

    [Fact]
    public async Task LinkedApp_WithBaseUrl_ReturnsOtelEnv_AndRotatesKey()
    {
        var env = await Build("https://obs.example.com").GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);

        Assert.Equal("https://obs.example.com/api/ingest/otlp/v1/metrics", env["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
        Assert.Equal("https://obs.example.com/api/ingest/otlp/v1/logs", env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"]);
        Assert.Equal("https://obs.example.com/api/ingest/otlp/v1/traces", env["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"]);
        Assert.Equal("http/protobuf", env["OTEL_EXPORTER_OTLP_PROTOCOL"]);
        Assert.StartsWith("x-aetheus-ingest-key=", env["OTEL_EXPORTER_OTLP_HEADERS"]);
        Assert.Equal("https://obs.example.com/api/ingest/visitors", env["AETHEUS_VISITOR_ENDPOINT"]);

        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(app.IngestKeyHash); // a fresh key was issued and stored (hashed)
        Assert.Equal(new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc), app.IngestKeyExpiresAt);

        // The plaintext key is registered for masking in the run's logs.
        _masking.Received(1).RegisterRuntimeSecret(7, Arg.Any<string>());
    }

    [Fact]
    public async Task SameRun_AcrossStages_ReusesOneKey()
    {
        // A production deployment calls this once per stage carrying execution_role: deploy - about ten
        // times. Rotating on each call left the container holding a key that rotation had already pushed
        // past "previous", so every OTLP export came back 401 and no application ever recorded a point.
        var provider = Build("https://obs.example.com");

        var first = await provider.GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);
        var second = await provider.GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);
        var third = await provider.GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);

        Assert.Equal(first["AETHEUS_INGEST_KEY"], second["AETHEUS_INGEST_KEY"]);
        Assert.Equal(first["AETHEUS_INGEST_KEY"], third["AETHEUS_INGEST_KEY"]);
        Assert.Equal(first["OTEL_EXPORTER_OTLP_HEADERS"], third["OTEL_EXPORTER_OTLP_HEADERS"]);

        // One rotation for the whole run, so the key handed to the container stays the current one.
        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, app.IngestKeyVersion);
    }

    [Fact]
    public async Task SameRun_AfterTheBackendRestarted_ReusesTheKeyWithoutRotating()
    {
        // A deployment key must remain stable across backend restarts within the same run so
        // containers keep valid credentials.
        var beforeRestart = await Build("https://obs.example.com")
            .GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);
        var afterRestart = await Build("https://obs.example.com")
            .GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);

        Assert.Equal(beforeRestart["AETHEUS_INGEST_KEY"], afterRestart["AETHEUS_INGEST_KEY"]);
        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, app.IngestKeyVersion);
        Assert.Equal(_hasher.Hash(afterRestart["AETHEUS_INGEST_KEY"]), app.IngestKeyHash);
    }

    [Fact]
    public async Task DifferentRuns_StillRotate()
    {
        // Reuse is scoped to one run: a later deployment must not keep issuing the key of an earlier one.
        var provider = Build("https://obs.example.com");

        var runSeven = await provider.GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);
        var runEight = await provider.GetDeployEnvAsync(1, null, pipelineRunId: 8, ct: TestContext.Current.CancellationToken);

        Assert.NotEqual(runSeven["AETHEUS_INGEST_KEY"], runEight["AETHEUS_INGEST_KEY"]);
        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, app.IngestKeyVersion);
    }

    [Fact]
    public async Task NoBaseUrl_ReturnsEmpty_AndDoesNotTouchApp()
    {
        var env = await Build(baseUrl: null).GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);
        Assert.Empty(env);
        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(app.IngestKeyHash);
        _masking.DidNotReceive().RegisterRuntimeSecret(Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public async Task NoLinkedApp_ReturnsEmpty()
    {
        var env = await Build("https://obs.example.com").GetDeployEnvAsync(projectId: 999, null, pipelineRunId: 7, ct: TestContext.Current.CancellationToken);
        Assert.Empty(env);
    }

    [Fact]
    public async Task DisabledLegacyApp_IsNotSelectedOverActiveEnvironmentApp()
    {
        _db.MonitoredApps.AddRange(
            new MonitoredApp
            {
                Id = 2,
                ProjectId = 1,
                EnvironmentId = 7,
                Name = "Portfolio",
                Enabled = false
            },
            new MonitoredApp
            {
                Id = 3,
                ProjectId = 1,
                EnvironmentId = 7,
                Name = "Aetheus",
                Enabled = true
            });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var env = await Build("https://obs.example.com").GetDeployEnvAsync(
            1,
            environmentId: 7,
            pipelineRunId: 7,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("3", env["AETHEUS_TELEMETRY_APPLICATION_ID"]);
        Assert.Equal("Aetheus", env["OTEL_SERVICE_NAME"]);
    }

    [Fact]
    public async Task AnalyticsEnabled_InjectsPackageConfigurationAndMasksBothSecrets()
    {
        var app = await _db.MonitoredApps.SingleAsync(
            item => item.Id == 1,
            TestContext.Current.CancellationToken);
        app.AnalyticsEnabled = true;
        app.AnalyticsSiteId = "portfolio-prod";
        app.AnalyticsVaultName = "aetheus-web-analytics-1";
        app.AnalyticsPseudonymKeyVersion = 3;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _vaults.ResolveVaultSecretsAsync(
                Arg.Any<List<string>>(),
                1,
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                [AppWebAnalyticsConfigurationService.SecretKey] =
                    "0123456789abcdef0123456789abcdef"
            });

        var env = await Build("https://obs.example.com").GetDeployEnvAsync(
            1,
            null,
            pipelineRunId: 7,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("true", env["AETHEUS_WEB_ANALYTICS_ENABLED"]);
        Assert.False(string.IsNullOrWhiteSpace(env["AETHEUS_INGEST_KEY"]));
        Assert.Equal("portfolio-prod", env["AETHEUS_WEB_ANALYTICS_SITE_ID"]);
        Assert.Equal(
            "https://obs.example.com/api/ingest/web-analytics/v1/events",
            env["AETHEUS_WEB_ANALYTICS_INGEST_ENDPOINT"]);
        Assert.Equal("3", env["AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_VERSION"]);
        Assert.Equal(2, _masking.ReceivedCalls().Count());
        // The blue-green binding drops exactly these names on the steps that do not start a colour:
        // one emitted here and missing from the list would make the strict migrate step refuse it.
        Assert.Empty(env.Keys.Except(AppDeployEnvProvider.DerivedNames, StringComparer.Ordinal));
        Assert.Empty(AppDeployEnvProvider.DerivedNames.Except(env.Keys, StringComparer.Ordinal));
    }

    private async Task<int?> ResolveAsync(string plaintext, DateTimeOffset at) =>
        (await new AppMonitoringRepository(_db).ResolveIngestKeyHashAsync(
            _hasher.Hash(plaintext), at.UtcDateTime, TestContext.Current.CancellationToken))?.AppId;

    [Fact]
    public async Task R2_013_TheIdleColourKey_StaysValid_WhileTheNextRunRotates()
    {
        // Run 7 started blue, run 8 started green. When run 9 rotates, green (active) holds run 8's key
        // and blue (idle, still running until run 9 replaces it) holds run 7's: both must still resolve.
        var provider = Build("https://obs.example.com");
        var ct = TestContext.Current.CancellationToken;
        var blue = (await provider.GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: ct))["AETHEUS_INGEST_KEY"];
        var green = (await provider.GetDeployEnvAsync(1, null, pipelineRunId: 8, ct: ct))["AETHEUS_INGEST_KEY"];
        var next = (await provider.GetDeployEnvAsync(1, null, pipelineRunId: 9, ct: ct))["AETHEUS_INGEST_KEY"];

        Assert.Equal(1, await ResolveAsync(blue, T0));
        Assert.Equal(1, await ResolveAsync(green, T0));
        Assert.Equal(1, await ResolveAsync(next, T0));
    }

    [Fact]
    public async Task R2_013_AKeyThreeRunsOld_IsRefused_AndLeavesTheCache()
    {
        // Run 9 replaced the colour run 7 started: run 10's rotation drops run 7's key.
        var provider = Build("https://obs.example.com");
        var ct = TestContext.Current.CancellationToken;
        var oldest = (await provider.GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: ct))["AETHEUS_INGEST_KEY"];
        await provider.GetDeployEnvAsync(1, null, pipelineRunId: 8, ct: ct);
        await provider.GetDeployEnvAsync(1, null, pipelineRunId: 9, ct: ct);
        _ingest.ClearReceivedCalls();

        await provider.GetDeployEnvAsync(1, null, pipelineRunId: 10, ct: ct);

        Assert.Null(await ResolveAsync(oldest, T0));
        _ingest.Received(1).InvalidateKeyCache(_hasher.Hash(oldest));
    }

    [Fact]
    public async Task R2_013_TheKeptKey_KeepsTheDeadlineItWasGiven_NotANewOne()
    {
        // A key never lives longer than the overlap window it received when it stopped being current.
        var ct = TestContext.Current.CancellationToken;
        var first = (await Build("https://obs.example.com", T0).GetDeployEnvAsync(1, null, pipelineRunId: 7, ct: ct))["AETHEUS_INGEST_KEY"];
        await Build("https://obs.example.com", T0.AddDays(1)).GetDeployEnvAsync(1, null, pipelineRunId: 8, ct: ct);
        await Build("https://obs.example.com", T0.AddDays(2)).GetDeployEnvAsync(1, null, pipelineRunId: 9, ct: ct);

        var app = await _db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == 1, ct);
        var deadline = T0.AddDays(1 + AppMonitoringDefaults.DefaultIngestKeyOverlapDays);
        Assert.Equal(_hasher.Hash(first), app.SecondPreviousIngestKeyHash);
        Assert.Equal(deadline.UtcDateTime, app.SecondPreviousIngestKeyValidUntil);
        Assert.Equal(1, await ResolveAsync(first, deadline));
        Assert.Null(await ResolveAsync(first, deadline.AddSeconds(1)));
    }

    [Fact]
    public void EveryDerivedName_IsDroppedFromTheMigrateStepByTheBlueGreenBinding() =>
        Assert.All(AppDeployEnvProvider.DerivedNames,
            name => Assert.Contains(name, Aetheus.Back.Components.Pipelines.BlueGreenStepBinding.StartOnlyNames));
}
