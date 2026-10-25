// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Middleware;

/// <summary>
/// Instruments the intermittent pauses of recette R1-40 (calls that normally answer in a few
/// milliseconds taking exactly 2 s, on several routes at once): when a request lasts at least
/// <see cref="SlowRequestDiagnosticsOptions.Threshold"/>, one Warning line says what the process was
/// doing meanwhile, so the cause is read instead of guessed. It records nothing else and changes no
/// behaviour; an ordinary request pays a few counter reads and one clock read.
/// </summary>
/// <remarks>
/// The figures are process-wide, not the request's own: a garbage collection pause that overlaps the
/// request is reported even when another request caused it, which is what a pause looks like from
/// outside. Read it together with the other slow requests logged at the same instant.
/// <para>
/// Two figures say where a slow request waited (R-6). "Host pressure" is the Linux PSI avg10 percentage of
/// cpu, io (some and full) and memory, host-wide: a high figure means the HOST was starved (CPU or disk I/O
/// contention, for example a pipeline build on the same VPS). "Database" is the request's own command count,
/// summed and longest command time and summed commit time, measured by EF Core: a database time close to
/// the elapsed time means the DATABASE was slow. When both are low, the time went elsewhere, in the request
/// body or the network. Connection open wait is not in the database figure, the pool figures cover it.
/// </para>
/// </remarks>
internal sealed class SlowRequestDiagnosticsMiddleware(
    RequestDelegate next,
    IOptions<SlowRequestDiagnosticsOptions> options,
    ConnectionPoolProbe pool,
    BackgroundWorkTracker background,
    HostPressureReader hostPressure,
    TimeProvider time,
    ILogger<SlowRequestDiagnosticsMiddleware> logger)
{
    private readonly TimeSpan _threshold = options.Value.Threshold;

    public async Task InvokeAsync(HttpContext context)
    {
        // A live connection (SignalR, a streamed response) is long by nature, not slow.
        if (_threshold <= TimeSpan.Zero || IsLongLived(context))
        {
            await next(context);
            return;
        }

        var startedAt = time.GetUtcNow();
        var startedStamp = time.GetTimestamp();
        var runtimeBefore = RuntimePauseSnapshot.Capture();
        var poolBefore = pool.Totals();
        var database = RequestDatabaseTiming.Begin();
        try
        {
            await next(context);
        }
        finally
        {
            RequestDatabaseTiming.End();
            var elapsed = time.GetElapsedTime(startedStamp);
            if (elapsed >= _threshold)
                Report(context, startedAt, elapsed, runtimeBefore, poolBefore, database);
        }
    }

    private void Report(
        HttpContext context,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        RuntimePauseSnapshot runtimeBefore,
        ConnectionPoolProbe.PoolTotals poolBefore,
        RequestDatabaseTime database)
    {
        var runtime = RuntimePauseSnapshot.Capture().Since(runtimeBefore);
        var poolDelta = pool.Totals().Since(poolBefore);
        var levels = pool.Levels();
        var host = hostPressure.Read();
        logger.LogWarning(
            "Slow request {Method} {Path}: {ElapsedMs} ms (threshold {ThresholdMs} ms), status {StatusCode}. "
            + "GC pause {GcPauseMs} ms, collections gen0/gen1/gen2 {Gen0}/{Gen1}/{Gen2}. "
            + "Thread pool {ThreadPoolThreads} threads, {ThreadPoolPending} queued work items, {LockContentions} lock contentions. "
            + "Npgsql pool {PoolPending} requests waiting, {PoolInUse} connections in use, {PoolIdle} idle, "
            + "{PoolCreated} created in {PoolCreateMs} ms, {PoolTimeouts} timeouts. "
            + "Database {DbCommands} commands in {DbMs} ms (longest {DbLongestMs} ms), commits {DbCommitMs} ms. "
            + "Host pressure cpu {PsiCpu}, io {PsiIo} (full {PsiIoFull}), memory {PsiMemory}. "
            + "Background work {BackgroundWork}; correlation {CorrelationId}",
            context.Request.Method,
            context.Request.Path,
            (long)elapsed.TotalMilliseconds,
            (long)_threshold.TotalMilliseconds,
            context.Response.StatusCode,
            (long)runtime.GcPause.TotalMilliseconds,
            runtime.Gen0Collections,
            runtime.Gen1Collections,
            runtime.Gen2Collections,
            runtime.ThreadPoolThreads,
            runtime.ThreadPoolPendingWorkItems,
            runtime.LockContentions,
            levels.PendingRequests,
            levels.InUse,
            levels.Idle,
            poolDelta.Created,
            (long)poolDelta.CreateTime.TotalMilliseconds,
            poolDelta.Timeouts,
            database.Commands,
            (long)database.CommandTime.TotalMilliseconds,
            (long)database.LongestCommand.TotalMilliseconds,
            (long)database.CommitTime.TotalMilliseconds,
            HostPressureSnapshot.Format(host.Cpu.Some),
            HostPressureSnapshot.Format(host.Io.Some),
            HostPressureSnapshot.Format(host.Io.Full),
            HostPressureSnapshot.Format(host.Memory.Some),
            Describe(background.Overlapping(startedAt)),
            context.TraceIdentifier);
    }

    internal static string Describe(IReadOnlyList<string> overlapping) =>
        overlapping.Count == 0 ? "none tracked" : string.Join(", ", overlapping);

    private static bool IsLongLived(HttpContext context) =>
        context.WebSockets.IsWebSocketRequest
        || context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
        || context.Request.Headers.Accept.Any(value => value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
}

/// <summary>Configuration of <see cref="SlowRequestDiagnosticsMiddleware"/>, bound from "SlowRequestDiagnostics".</summary>
internal sealed class SlowRequestDiagnosticsOptions
{
    /// <summary>A request at least this long is reported; zero or less switches the diagnostics off.</summary>
    public TimeSpan Threshold { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>A pool connection creation at least this long is reported on its own line (R-6); zero or less
    /// switches that line off.</summary>
    public TimeSpan SlowConnectionCreationThreshold { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often a fresh, unpooled connection is opened and timed phase by phase (R-6); zero or less
    /// switches the probe off.</summary>
    public TimeSpan ConnectionProbeInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A probe connection at least this long (or failing) is reported with its phases.</summary>
    public TimeSpan ConnectionProbeThreshold { get; set; } = TimeSpan.FromMilliseconds(500);
}

internal static class SlowRequestDiagnosticsExtensions
{
    public static IServiceCollection AddSlowRequestDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlowRequestDiagnosticsOptions>(configuration.GetSection("SlowRequestDiagnostics"));
        services.AddSingleton<ConnectionPoolProbe>();
        services.AddSingleton(BackgroundWorkTracker.Shared);
        services.AddSingleton<HostPressureReader>();
        services.AddSingleton<RequestDatabaseTimingCommandInterceptor>();
        services.AddSingleton<RequestDatabaseTimingTransactionInterceptor>();
        services.AddHostedService<DatabaseConnectionOpenProbeService>();
        return services;
    }
}
