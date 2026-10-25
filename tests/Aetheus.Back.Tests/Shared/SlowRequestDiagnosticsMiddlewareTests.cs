// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Middleware;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>
/// R1-40: a request that lasts past the threshold says, in one Warning line, what the process was doing
/// meanwhile (GC pause and collections, thread pool, Npgsql pool, tracked background work). A fast
/// request writes nothing, and the instrumentation never changes the response or swallows an error.
/// </summary>
public sealed class SlowRequestDiagnosticsMiddlewareTests : IDisposable
{
    private readonly string _pressureDirectory = Path.Combine(Path.GetTempPath(), "aetheus-slow-psi-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(1500);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 13, 51, 12, TimeSpan.Zero));
    private readonly RecordingLogger<SlowRequestDiagnosticsMiddleware> _log = new();
    private readonly BackgroundWorkTracker _background;
    private readonly ConnectionPoolProbe _pool = new("slow-request-tests-no-such-meter");

    public SlowRequestDiagnosticsMiddlewareTests() => _background = new BackgroundWorkTracker(_clock);

    public void Dispose()
    {
        _pool.Dispose();
        if (Directory.Exists(_pressureDirectory))
            Directory.Delete(_pressureDirectory, recursive: true);
    }

    private SlowRequestDiagnosticsMiddleware Sut(RequestDelegate next, TimeSpan? threshold = null) => new(
        next,
        Options.Create(new SlowRequestDiagnosticsOptions { Threshold = threshold ?? Threshold }),
        _pool,
        _background,
        new HostPressureReader(_pressureDirectory),
        _clock,
        _log);

    private static DefaultHttpContext Context(string path = "/api/projects/7")
    {
        var context = new DefaultHttpContext { TraceIdentifier = "request-42" };
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.StatusCode = StatusCodes.Status200OK;
        return context;
    }

    private RequestDelegate TakesMilliseconds(int milliseconds) => _ =>
    {
        _clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        return Task.CompletedTask;
    };

    private IReadOnlyList<string> Slow() => [.. _log.Entries
        .Where(entry => entry.Message.StartsWith("Slow request", StringComparison.Ordinal))
        .Select(entry => entry.Message)];

    [Fact]
    public async Task AFastRequest_WritesNothing()
    {
        await Sut(TakesMilliseconds(200)).InvokeAsync(Context());

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task ARequestJustUnderTheThreshold_WritesNothing_AndOneAtTheThresholdIsReported()
    {
        await Sut(TakesMilliseconds(1499)).InvokeAsync(Context());
        Assert.Empty(Slow());

        await Sut(TakesMilliseconds(1500)).InvokeAsync(Context());
        Assert.Single(Slow());
    }

    [Fact]
    public async Task ASlowRequest_IsOneWarning_NamingTheRequestItsDurationAndTheCorrelation()
    {
        await Sut(TakesMilliseconds(2050)).InvokeAsync(Context("/api/releases"));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("GET /api/releases: 2050 ms (threshold 1500 ms), status 200", entry.Message, StringComparison.Ordinal);
        Assert.Contains("correlation request-42", entry.Message, StringComparison.Ordinal);
        // Every family of evidence the R1-40 analysis asks for is on the line.
        Assert.Contains("GC pause", entry.Message, StringComparison.Ordinal);
        Assert.Contains("collections gen0/gen1/gen2", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Thread pool", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Npgsql pool", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Background work none tracked", entry.Message, StringComparison.Ordinal);
        // R-6: the two figures that say whether the request waited on the host or on the database.
        Assert.Contains("Database 0 commands in 0 ms (longest 0 ms), commits 0 ms.", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Host pressure cpu n/a, io n/a (full n/a), memory n/a.", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowRequest_ReportsItsOwnDatabaseTime()
    {
        await Sut(async context =>
        {
            RequestDatabaseTiming.RecordCommand(TimeSpan.FromMilliseconds(300));
            await Task.Yield();
            RequestDatabaseTiming.RecordCommand(TimeSpan.FromMilliseconds(900));
            RequestDatabaseTiming.RecordCommit(TimeSpan.FromMilliseconds(45));
            await TakesMilliseconds(2000)(context);
        }).InvokeAsync(Context());

        Assert.Contains("Database 2 commands in 1200 ms (longest 900 ms), commits 45 ms.", Assert.Single(Slow()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDatabaseTimeOfAnotherRequest_IsNotCarriedOver()
    {
        await Sut(async context =>
        {
            RequestDatabaseTiming.RecordCommand(TimeSpan.FromMilliseconds(700));
            await TakesMilliseconds(2000)(context);
        }).InvokeAsync(Context());
        await Sut(TakesMilliseconds(2000)).InvokeAsync(Context());

        Assert.Contains("Database 0 commands in 0 ms", Slow()[1], StringComparison.Ordinal);
        Assert.Null(RequestDatabaseTiming.Current);
    }

    [Fact]
    public async Task ASlowRequest_ReportsTheHostPressure_WithTheInvariantCulture()
    {
        Directory.CreateDirectory(_pressureDirectory);
        File.WriteAllLines(Path.Combine(_pressureDirectory, "cpu"), ["some avg10=12.50 avg60=3.00 avg300=1.00 total=1"]);
        File.WriteAllLines(Path.Combine(_pressureDirectory, "io"),
            ["some avg10=45.00 avg60=0 avg300=0 total=1", "full avg10=30.25 avg60=0 avg300=0 total=1"]);
        File.WriteAllLines(Path.Combine(_pressureDirectory, "memory"), ["some avg10=0.00 avg60=0 avg300=0 total=1"]);

        await Sut(TakesMilliseconds(2000)).InvokeAsync(Context());

        Assert.Contains("Host pressure cpu 12.5, io 45 (full 30.25), memory 0.", Assert.Single(Slow()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFastRequest_NeverReadsTheHostPressure()
    {
        var reader = new CountingPressureReader(_pressureDirectory);
        var sut = new SlowRequestDiagnosticsMiddleware(
            TakesMilliseconds(200),
            Options.Create(new SlowRequestDiagnosticsOptions { Threshold = Threshold }),
            _pool, _background, reader, _clock, _log);

        await sut.InvokeAsync(Context());
        Assert.Equal(0, reader.Reads);

        await new SlowRequestDiagnosticsMiddleware(
            TakesMilliseconds(2000),
            Options.Create(new SlowRequestDiagnosticsOptions { Threshold = Threshold }),
            _pool, _background, reader, _clock, _log).InvokeAsync(Context());
        Assert.Equal(1, reader.Reads);
    }

    private sealed class CountingPressureReader(string directory) : HostPressureReader(directory)
    {
        public int Reads { get; private set; }

        public override HostPressureSnapshot Read()
        {
            Reads++;
            return base.Read();
        }
    }

    [Fact]
    public async Task ACollectionDuringTheRequest_ShowsInTheCollectionCounts()
    {
        await Sut(async context =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            await TakesMilliseconds(2000)(context);
        }).InvokeAsync(Context());

        var line = Assert.Single(Slow());
        // The counts are process-wide and other tests may collect too: at least the one forced here.
        var counts = line[(line.IndexOf("gen0/gen1/gen2 ", StringComparison.Ordinal) + "gen0/gen1/gen2 ".Length)..]
            .Split('.')[0].Split('/').Select(int.Parse).ToArray();
        Assert.True(counts[2] >= 1, line);
    }

    [Fact]
    public async Task BackgroundWorkThatOverlappedTheRequest_IsNamed()
    {
        using var running = _background.Begin("TaskTimeoutService");
        var ended = _background.Begin("PipelineRetentionService");
        ended.Dispose();

        await Sut(TakesMilliseconds(2000)).InvokeAsync(Context());

        var line = Assert.Single(Slow());
        Assert.Contains("PipelineRetentionService (ended)", line, StringComparison.Ordinal);
        Assert.Contains("TaskTimeoutService (running)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundWorkThatEndedBeforeTheRequest_IsNotNamed()
    {
        _background.Begin("OldService").Dispose();
        _clock.Advance(TimeSpan.FromSeconds(30));

        await Sut(TakesMilliseconds(2000)).InvokeAsync(Context());

        Assert.Contains("Background work none tracked", Assert.Single(Slow()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowRequestThatFails_IsStillReported_AndTheExceptionReachesTheCaller()
    {
        var failure = new InvalidOperationException("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Sut(_ =>
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            throw failure;
        }).InvokeAsync(Context()));

        Assert.Same(failure, thrown);
        Assert.Single(Slow());
    }

    [Theory]
    [InlineData("/hubs/servers")]
    [InlineData("/HUBS/pipelines")]
    public async Task ALiveHubConnection_IsNeverReportedAsSlow(string path)
    {
        await Sut(TakesMilliseconds(60_000)).InvokeAsync(Context(path));

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task AnEventStream_IsNeverReportedAsSlow()
    {
        var context = Context("/api/logs/stream");
        context.Request.Headers.Accept = "text/event-stream";

        await Sut(TakesMilliseconds(60_000)).InvokeAsync(context);

        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AThresholdOfZeroOrLess_SwitchesTheDiagnosticsOff_WithoutChangingTheRequest(int milliseconds)
    {
        var context = Context();

        await Sut(ctx =>
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(milliseconds)).InvokeAsync(context);

        Assert.Empty(_log.Entries);
        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
    }
}
