// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Microsoft.Extensions.Logging;

namespace Aetheus.WebAnalytics.Tests;

/// <summary> R1-11: an ingest key Aetheus refuses (401 or 403) is not sent again with every batch. The
/// exporter drops the batches (counted), says so once, and goes on only after the back-off or with
/// another key. A transient answer (429, 5xx) does not suspend anything. </summary>
public sealed class AnalyticsIngestRefusalTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task APermanentRefusal_StopsTheRequests_AndIsLoggedOnce(HttpStatusCode refusal)
    {
        var rig = new Rig(refusal);

        for (var batch = 0; batch < 5; batch++)
            await rig.ExportAsync();

        Assert.Equal(1, rig.Transport.Calls);
        var warning = Assert.Single(rig.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("https://aetheus.test/api/ingest/web-analytics/v1/events", warning.Message, StringComparison.Ordinal);
        Assert.Contains("refused the ingest key", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-key", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ATransientAnswer_SuspendsNothing_TheNextBatchIsSent(HttpStatusCode transient)
    {
        var rig = new Rig(transient);

        for (var batch = 0; batch < 4; batch++)
            await rig.ExportAsync();

        Assert.Equal(4, rig.Transport.Calls);
        Assert.Empty(rig.Logger.Entries);
    }

    [Fact]
    public async Task AChangedKey_IsSentAtOnce_AndAcceptedResumesTheExports()
    {
        var rig = new Rig(HttpStatusCode.Unauthorized);
        await rig.ExportAsync();
        await rig.ExportAsync();
        Assert.Equal(1, rig.Transport.Calls);

        rig.Transport.Status = HttpStatusCode.Accepted;
        rig.Options.IngestKey = "new-key";
        await rig.ExportAsync();
        await rig.ExportAsync();

        Assert.Equal(3, rig.Transport.Calls);
        Assert.Equal("new-key", rig.Transport.LastKey);
        Assert.Contains(rig.Logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("accepted again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedKey_IsTriedOnceMore_WhenTheBackoffEnds()
    {
        var rig = new Rig(HttpStatusCode.Unauthorized);
        await rig.ExportAsync();
        rig.Time.Advance(AnalyticsIngestRefusal.Backoff - TimeSpan.FromSeconds(1));
        await rig.ExportAsync();
        Assert.Equal(1, rig.Transport.Calls);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.ExportAsync();
        await rig.ExportAsync();

        Assert.Equal(2, rig.Transport.Calls);
        Assert.Single(rig.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task EveryRequest_NamesItsEmitter_InTheUserAgent()
    {
        var rig = new Rig(HttpStatusCode.Accepted);

        await rig.ExportAsync();

        Assert.Contains("Aetheus.WebAnalytics/", rig.Transport.LastUserAgent, StringComparison.Ordinal);
        Assert.Contains("site my-site", rig.Transport.LastUserAgent, StringComparison.Ordinal);
        Assert.Contains("application 42", rig.Transport.LastUserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-key", rig.Transport.LastUserAgent, StringComparison.Ordinal);
    }

    private sealed class Rig
    {
        public Rig(HttpStatusCode status)
        {
            Transport = new StubTransport(status);
            Options = new AetheusWebAnalyticsOptions
            {
                ApplicationId = 42,
                SiteId = "my-site",
                IngestEndpoint = new Uri("https://aetheus.test/api/ingest/web-analytics/v1/events"),
                IngestKey = "stale-key"
            };
            Service = new AnalyticsExportService(
                new AnalyticsExportQueue(),
                new AnalyticsHttpClientFactory(new HttpClient(Transport)),
                Options,
                Logger,
                Time);
        }

        public StubTransport Transport { get; }
        public AetheusWebAnalyticsOptions Options { get; }
        public RecordingLogger Logger { get; } = new();
        public ManualTime Time { get; } = new();
        private AnalyticsExportService Service { get; }

        public Task ExportAsync() => Service.ExportAsync([new AnalyticsExportEvent { EventId = Guid.NewGuid() }], CancellationToken.None);
    }

    internal sealed class StubTransport(HttpStatusCode status) : HttpMessageHandler
    {
        private int _calls;

        public HttpStatusCode Status { get; set; } = status;
        public int Calls => Volatile.Read(ref _calls);
        public string? LastKey { get; private set; }
        public string LastUserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastKey = request.Headers.TryGetValues("x-aetheus-ingest-key", out var keys) ? keys.Single() : null;
            LastUserAgent = string.Join(' ', request.Headers.GetValues("User-Agent"));
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    internal sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    internal sealed class RecordingLogger : ILogger<AnalyticsExportService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
