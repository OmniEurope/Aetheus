// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Aetheus.Telemetry.Tests;

/// <summary> The package sends its metrics at the interval Aetheus hands over with the ingest key,
/// and keeps its own interval when the server says nothing (an older Aetheus, a network failure).
/// </summary>
public sealed class MetricExportScheduleTests
{
    private static readonly Uri MetricsEndpoint = new("https://aetheus.test/api/ingest/otlp/v1/metrics");

    [Fact]
    public void TheConfigurationEndpoint_IsNextToTheMetricsEndpoint()
    {
        Assert.Equal(
            new Uri("https://aetheus.test/api/ingest/otlp/v1/configuration"),
            ServerConfigurationClient.ConfigurationUriFor(MetricsEndpoint));
    }

    [Fact]
    public async Task TheClient_ReadsTheIntervalWithTheIngestKey()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"metricExportIntervalSeconds":900}""");

        var seconds = await Client(handler).ReadMetricExportIntervalSecondsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(900, seconds);
        Assert.Equal("secret-key", Assert.Single(handler.Request!.Headers.GetValues(ServerConfigurationClient.KeyHeader)));
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"metricExportIntervalSeconds":0}""")]
    public async Task AServerThatSaysNothingUsable_LeavesNoInterval(HttpStatusCode status, string body)
    {
        var seconds = await Client(new StubHandler(status, body))
            .ReadMetricExportIntervalSecondsAsync(TestContext.Current.CancellationToken);

        Assert.Null(seconds);
    }

    [Fact]
    public async Task ANetworkFailure_LeavesNoInterval()
    {
        var seconds = await Client(new StubHandler(new HttpRequestException("refused")))
            .ReadMetricExportIntervalSecondsAsync(TestContext.Current.CancellationToken);

        Assert.Null(seconds);
    }

    [Fact]
    public void TheSchedule_StartsAtTheOpenTelemetryDefault_AndKeepsAppliedValuesInBounds()
    {
        var schedule = new MetricExportSchedule(MetricExportSchedule.DefaultInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), schedule.Interval);

        schedule.Apply(900);
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Interval);
        schedule.Apply(1);
        Assert.Equal(MetricExportSchedule.MinimumInterval, schedule.Interval);
        schedule.Apply(86_400);
        Assert.Equal(MetricExportSchedule.MaximumInterval, schedule.Interval);
    }

    [Fact]
    public async Task TheScheduler_AppliesTheServerInterval_AndKeepsItsOwnWhenTheServerIsSilent()
    {
        var applied = new MetricExportSchedule(MetricExportSchedule.DefaultInterval);
        await Scheduler(applied, new StubHandler(HttpStatusCode.OK, """{"metricExportIntervalSeconds":900}"""))
            .RefreshIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(15), applied.Interval);

        var silent = new MetricExportSchedule(MetricExportSchedule.DefaultInterval);
        await Scheduler(silent, new StubHandler(HttpStatusCode.NotFound, ""))
            .RefreshIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(60), silent.Interval);
    }

    [Fact]
    public async Task TheScheduler_ReadsTheServerOncePerRefreshPeriod()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"metricExportIntervalSeconds":300}""");
        var scheduler = Scheduler(new MetricExportSchedule(MetricExportSchedule.DefaultInterval), handler);

        await scheduler.RefreshIfDueAsync(TestContext.Current.CancellationToken);
        await scheduler.RefreshIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void TheReader_ExportsOnlyWhenTheSchedulerCollects()
    {
        using var meter = new Meter($"aetheus.test.{Guid.NewGuid():N}");
        var exporter = new CountingExporter();
        var reader = new BaseExportingMetricReader(exporter);
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(meter.Name).AddReader(reader).Build();
        meter.CreateCounter<long>("requests").Add(3);

        Thread.Sleep(200);
        Assert.Equal(0, exporter.Exports);

        reader.Collect();
        Assert.Equal(1, exporter.Exports);
    }

    [Fact]
    public void AddAetheusTelemetry_RegistersTheScheduledMetricExport()
    {
        var previous = Environment.GetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED");
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAetheusTelemetry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aetheus:Telemetry:Enabled"] = "true",
                ["Aetheus:Telemetry:ApplicationName"] = "app",
                ["Aetheus:Telemetry:ApplicationId"] = "1",
                ["Aetheus:Telemetry:MetricsEndpoint"] = MetricsEndpoint.ToString(),
                ["Aetheus:Telemetry:TracesEndpoint"] = "https://aetheus.test/api/ingest/otlp/v1/traces",
                ["Aetheus:Telemetry:IngestKey"] = "secret-key"
            }).Build());

            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<MetricExportSchedule>());
            Assert.Contains(provider.GetServices<IHostedService>(), service => service is MetricExportScheduler);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", previous);
        }
    }

    private static ServerConfigurationClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), ServerConfigurationClient.ConfigurationUriFor(MetricsEndpoint), "secret-key");

    private static MetricExportScheduler Scheduler(MetricExportSchedule schedule, HttpMessageHandler handler) =>
        new(new BaseExportingMetricReader(new CountingExporter()), schedule, Client(handler), TimeProvider.System);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body = string.Empty;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body) => (_status, _body) = (status, body);
        public StubHandler(Exception failure) => _failure = failure;

        public HttpRequestMessage? Request { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Calls++;
            if (_failure is not null) throw _failure;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CountingExporter : BaseExporter<Metric>
    {
        public int Exports { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Exports++;
            return ExportResult.Success;
        }
    }
}
