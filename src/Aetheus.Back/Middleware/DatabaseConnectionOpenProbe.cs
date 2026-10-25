// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Services;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aetheus.Back.Middleware;

/// <summary>What opening one brand-new PostgreSQL connection cost, phase by phase. A phase that was not
/// reached (the one before it failed, or the host is a Unix socket) is <c>null</c>.</summary>
internal sealed record ConnectionOpenTimings(
    string Host,
    int Port,
    TimeSpan? Dns,
    TimeSpan? TcpConnect,
    TimeSpan? ProtocolOpen,
    TimeSpan? FirstQuery,
    TimeSpan Total,
    string? Failure);

/// <summary>
/// Opens a connection that bypasses the pool and times each phase on its own: name resolution, the TCP
/// connect, then the whole Npgsql open (which resolves, connects, negotiates TLS and authenticates again)
/// and a first query (recette R-6). The pauses of about two seconds seen on several routes at once coincide
/// with the pool creating connections; this says <em>where</em> a creation spends its time. Read
/// <see cref="ConnectionOpenTimings.Dns"/> and <see cref="ConnectionOpenTimings.TcpConnect"/> against
/// <see cref="ConnectionOpenTimings.ProtocolOpen"/>: a large resolution is the resolver, a large connect is the
/// network (a lost SYN is retried after a fixed delay), an open far above both is the server (fork,
/// authentication, TLS).
/// </summary>
internal static class DatabaseConnectionOpenMeasurement
{
    public static async Task<ConnectionOpenTimings> MeasureAsync(
        string connectionString, TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        // A comma-separated host list is a failover list: the first entry is the one to time.
        var host = (builder.Host ?? string.Empty).Split(',', StringSplitOptions.TrimEntries)[0];
        var port = builder.Port;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        var started = time.GetTimestamp();
        TimeSpan? dns = null, tcp = null, open = null, query = null;
        try
        {
            if (host.Length > 0 && !host.StartsWith('/'))
            {
                var addresses = await ResolveAsync(host, bounded.Token).ConfigureAwait(false);
                dns = time.GetElapsedTime(started);

                var connectStarted = time.GetTimestamp();
                using var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(addresses[0], port, bounded.Token).ConfigureAwait(false);
                tcp = time.GetElapsedTime(connectStarted);
            }

            var openStarted = time.GetTimestamp();
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(bounded.Token).ConfigureAwait(false);
            open = time.GetElapsedTime(openStarted);

            var queryStarted = time.GetTimestamp();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(bounded.Token).ConfigureAwait(false);
            query = time.GetElapsedTime(queryStarted);

            return new ConnectionOpenTimings(host, port, dns, tcp, open, query, time.GetElapsedTime(started), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is NpgsqlException or SocketException or OperationCanceledException or InvalidOperationException)
        {
            // Mandatory catch: this is a measurement, the reason it could not finish is part of the result.
            var failure = ex is OperationCanceledException ? $"timed out after {timeout.TotalSeconds:0} s" : $"{ex.GetType().Name}: {ex.Message}";
            return new ConnectionOpenTimings(host, port, dns, tcp, open, query, time.GetElapsedTime(started), failure);
        }
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses.Length > 0
            ? addresses
            : throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>
/// Opens a fresh PostgreSQL connection at a fixed interval and writes one Warning when it took at least
/// <see cref="SlowRequestDiagnosticsOptions.ConnectionProbeThreshold"/> or failed, with the time of each phase
/// (see <see cref="DatabaseConnectionOpenMeasurement"/>). Silent otherwise: it adds one connection a minute and
/// no log line on a healthy database. The slow requests of R-6 cannot say why a connection creation was slow;
/// this can, because it repeats exactly that operation at known instants.
/// </summary>
internal sealed class DatabaseConnectionOpenProbeService(
    IConfiguration configuration,
    IOptions<SlowRequestDiagnosticsOptions> options,
    BackgroundWorkTracker background,
    TimeProvider time,
    ILogger<DatabaseConnectionOpenProbeService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    // Only the live colour serves the traffic whose pauses are being explained, so only it probes.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunProbeLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:db-connection-probe", RunProbeLoopAsync, stoppingToken);

    private async Task RunProbeLoopAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ConnectionProbeInterval;
        var connectionString = configuration.GetConnectionString("Default");
        if (interval <= TimeSpan.Zero || string.IsNullOrWhiteSpace(connectionString))
            return;

        logger.LogInformation(
            "Database connection probe started: a new PostgreSQL connection is opened every {IntervalSeconds} s and reported when it takes {ThresholdMs} ms or more",
            (long)interval.TotalSeconds,
            (long)options.Value.ConnectionProbeThreshold.TotalMilliseconds);
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProbeOnceAsync(connectionString, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Database connection probe iteration failed; the next one will retry");
            }
        }
    }

    internal async Task ProbeOnceAsync(string connectionString, CancellationToken ct)
    {
        var startedAt = time.GetUtcNow();
        var gcBefore = GC.GetTotalPauseDuration();
        var timings = await DatabaseConnectionOpenMeasurement.MeasureAsync(
            connectionString, time, ProbeTimeout, ct).ConfigureAwait(false);
        var threshold = options.Value.ConnectionProbeThreshold;
        if (timings.Failure is null && timings.Total < threshold)
        {
            logger.LogDebug(
                "Database connection probe: {TotalMs} ms to {Host}:{Port}", (long)timings.Total.TotalMilliseconds, timings.Host, timings.Port);
            return;
        }

        logger.LogWarning(
            "Slow PostgreSQL connection open to {Host}:{Port}: {TotalMs} ms (threshold {ThresholdMs} ms), failure {Failure}. "
            + "Phases: DNS {DnsMs} ms, TCP connect {TcpMs} ms, Npgsql open {OpenMs} ms, first query {QueryMs} ms. "
            + "GC pause {GcPauseMs} ms during the probe. Background work {BackgroundWork}",
            timings.Host,
            timings.Port,
            (long)timings.Total.TotalMilliseconds,
            (long)threshold.TotalMilliseconds,
            timings.Failure ?? "none",
            Milliseconds(timings.Dns),
            Milliseconds(timings.TcpConnect),
            Milliseconds(timings.ProtocolOpen),
            Milliseconds(timings.FirstQuery),
            (long)(GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds,
            SlowRequestDiagnosticsMiddleware.Describe(background.Overlapping(startedAt)));
    }

    private static string Milliseconds(TimeSpan? phase) =>
        phase is { } value ? ((long)value.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a";
}
