// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Aetheus.Telemetry.Tests;

/// <summary> R1-11: an ingest key Aetheus refuses (401 or 403) is not sent again at every export
/// interval. The exporter stops, says so once, and goes on only after the back-off or with another
/// key. A transient answer (429, 5xx) keeps the SDK's usual retry. </summary>
public sealed class IngestRefusalTests
{
    private static readonly Uri Endpoint = new("https://aetheus.test/api/ingest/otlp/v1/traces?x=1");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task APermanentRefusal_StopsTheRequests_AndIsLoggedOnce(HttpStatusCode refusal)
    {
        var (client, transport, logger, _) = Build(refusal);

        using var first = await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var again = await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        }

        Assert.Equal(refusal, first.StatusCode);
        Assert.Equal(1, transport.Calls);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("https://aetheus.test/api/ingest/otlp/v1/traces", warning.Message, StringComparison.Ordinal);
        Assert.Contains("refused the ingest key", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-key", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x=1", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ATransientAnswer_IsNotHeldBack_SoTheExporterKeepsItsRetry(HttpStatusCode transient)
    {
        var (client, transport, logger, _) = Build(transient);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.SendAsync(Request("good-key"), TestContext.Current.CancellationToken);
            Assert.Equal(transient, response.StatusCode);
        }

        Assert.Equal(4, transport.Calls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task AChangedKey_IsSentAtOnce_AndAcceptedResumesTheExports()
    {
        var (client, transport, logger, _) = Build(HttpStatusCode.Unauthorized);
        using (await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken)) { }
        using (await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken)) { }
        Assert.Equal(1, transport.Calls);

        transport.Status = HttpStatusCode.OK;
        using var changed = await client.SendAsync(Request("new-key"), TestContext.Current.CancellationToken);
        using var resumed = await client.SendAsync(Request("new-key"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(3, transport.Calls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("accepts the ingest key again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedKey_IsTriedOnceMore_WhenTheBackoffEnds()
    {
        var (client, transport, logger, time) = Build(HttpStatusCode.Unauthorized);
        using (await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken)) { }
        time.Advance(IngestRefusalGate.Backoff - TimeSpan.FromSeconds(1));
        using (await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken)) { }
        Assert.Equal(1, transport.Calls);

        time.Advance(TimeSpan.FromSeconds(2));
        using (await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken)) { }
        using (await client.SendAsync(Request("stale-key"), TestContext.Current.CancellationToken)) { }

        Assert.Equal(2, transport.Calls);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void TheSynchronousPath_UsedByTheSdkExporter_IsGuardedToo()
    {
        var (client, transport, _, _) = Build(HttpStatusCode.Unauthorized);

        using (client.Send(Request("stale-key"), TestContext.Current.CancellationToken)) { }
        using var second = client.Send(Request("stale-key"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task EveryRequest_NamesItsEmitter_InTheUserAgent()
    {
        var (client, transport, _, _) = Build(HttpStatusCode.OK);

        using (await client.SendAsync(Request("good-key"), TestContext.Current.CancellationToken)) { }

        var userAgent = string.Join(' ', transport.LastRequest!.Headers.GetValues("User-Agent"));
        Assert.Contains("Aetheus.Telemetry/", userAgent, StringComparison.Ordinal);
        Assert.Contains("service billing-api", userAgent, StringComparison.Ordinal);
        Assert.Contains("application 42", userAgent, StringComparison.Ordinal);
        Assert.DoesNotContain("good-key", userAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmitterName_IsLimitedToHeaderSafeCharacters()
    {
        var options = new AetheusTelemetryOptions { ApplicationName = "bad name)\r\nX-Evil: 1", ApplicationId = 7 };

        var emitter = AetheusTelemetryBuilderExtensions.EmitterDescription(options);

        Assert.DoesNotContain('\r', emitter);
        Assert.DoesNotContain('\n', emitter);
        Assert.Equal(1, emitter.Count(character => character == ')'));
    }

    [Fact]
    public void TheStockTraceExporter_StopsSendingARefusedKey_AndKeepsSendingAfterAServerError()
    {
        // The real OpenTelemetry OTLP exporter, configured the way AddAetheusTelemetry does it, over a
        // stub transport: the refused key reaches the network once, however many batches follow.
        var transport = new StubTransport(HttpStatusCode.Unauthorized);
        var gate = new IngestRefusalGate(new ManualTime());
        var options = new AetheusTelemetryOptions
        {
            ApplicationName = "billing-api",
            ApplicationId = 42,
            IngestKey = "stale-key",
            ExportTimeoutMilliseconds = 3000
        };
        var source = new ActivitySource($"aetheus.test.{Guid.NewGuid():N}");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddOtlpExporter(exporter => AetheusTelemetryBuilderExtensions.ConfigureExporter(
                exporter, Endpoint, options, gate, () => transport))
            .Build();

        for (var batch = 0; batch < 4; batch++)
        {
            source.StartActivity("work")?.Dispose();
            Assert.True(provider.ForceFlush(10_000));
        }

        Assert.Equal(1, transport.Calls);

        var failing = new StubTransport(HttpStatusCode.ServiceUnavailable);
        var freshGate = new IngestRefusalGate(new ManualTime());
        using var retrying = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddOtlpExporter(exporter => AetheusTelemetryBuilderExtensions.ConfigureExporter(
                exporter, Endpoint, options, freshGate, () => failing))
            .Build();
        for (var batch = 0; batch < 3; batch++)
        {
            source.StartActivity("work")?.Dispose();
            Assert.True(retrying.ForceFlush(30_000));
        }

        // A server error never suspends the key: every batch still reaches the network.
        Assert.True(failing.Calls >= 3, $"a 503 must not suspend the key, saw {failing.Calls} request(s) for 3 batches");
    }

    private static (HttpClient Client, StubTransport Transport, RecordingLogger Logger, ManualTime Time) Build(
        HttpStatusCode status)
    {
        var time = new ManualTime();
        var logger = new RecordingLogger();
        var gate = new IngestRefusalGate(time) { Logger = logger };
        var transport = new StubTransport(status);
        var options = new AetheusTelemetryOptions { ApplicationName = "billing-api", ApplicationId = 42 };
        var client = new HttpClient(new IngestRefusalHandler(
            gate, AetheusTelemetryBuilderExtensions.EmitterDescription(options), transport));
        return (client, transport, logger, time);
    }

    private static HttpRequestMessage Request(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-aetheus-ingest-key", key);
        return request;
    }

    private sealed class StubTransport(HttpStatusCode status) : HttpMessageHandler
    {
        private int _calls;

        public HttpStatusCode Status { get; set; } = status;
        public int Calls => Volatile.Read(ref _calls);
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            return new HttpResponseMessage(Status) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    private sealed class RecordingLogger : ILogger
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
