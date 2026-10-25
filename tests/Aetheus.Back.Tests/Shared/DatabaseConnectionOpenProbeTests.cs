// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Middleware;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Tests;

/// <summary>
/// R-6: the probe times what a pool connection creation spends its time on, phase by phase, and speaks only
/// when a connection is slow or fails. The success path against a real PostgreSQL is in the integration suite
/// (<c>DatabaseConnectionOpenProbeIntegrationTests</c>); here the server is a socket that accepts and hangs up,
/// so DNS and TCP connect are measured and the open fails with its reason.
/// </summary>
public sealed class DatabaseConnectionOpenProbeTests
{
    // Listens on a free loopback port and closes every connection at once: the TCP connect succeeds, the
    // PostgreSQL handshake cannot.
    private static TcpListener HangUpServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // The test stopped the listener.
            }
        });
        return listener;
    }

    private static string ConnectionStringOf(TcpListener listener) =>
        $"Host=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};Database=none;Username=none;Password=none;Timeout=5;SSL Mode=Disable";

    [Fact]
    public async Task AnOpenThatFails_ReturnsThePhasesItReached_AndTheFailure()
    {
        var listener = HangUpServer();
        try
        {
            var timings = await DatabaseConnectionOpenMeasurement.MeasureAsync(
                ConnectionStringOf(listener), TimeProvider.System, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.NotNull(timings.Failure);
            Assert.NotNull(timings.Dns);
            Assert.NotNull(timings.TcpConnect);
            Assert.Null(timings.ProtocolOpen);
            Assert.Null(timings.FirstQuery);
            Assert.Equal("127.0.0.1", timings.Host);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AnUnreachablePort_FailsInTheTcpConnectPhase()
    {
        // Port 1 on loopback refuses at once: the failure is reported, DNS was done, the connect was not.
        var timings = await DatabaseConnectionOpenMeasurement.MeasureAsync(
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=5",
            TimeProvider.System, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotNull(timings.Failure);
        Assert.NotNull(timings.Dns);
        Assert.Null(timings.TcpConnect);
    }

    private static (DatabaseConnectionOpenProbeService Service, RecordingLogger<DatabaseConnectionOpenProbeService> Log) Sut(
        TimeSpan threshold)
    {
        var log = new RecordingLogger<DatabaseConnectionOpenProbeService>();
        var service = new DatabaseConnectionOpenProbeService(
            new ConfigurationBuilder().Build(),
            Options.Create(new SlowRequestDiagnosticsOptions { ConnectionProbeThreshold = threshold }),
            new BackgroundWorkTracker(TimeProvider.System),
            TimeProvider.System,
            log);
        return (service, log);
    }

    [Fact]
    public async Task AFailedProbe_IsReportedAsOneWarningWithItsPhasesAndTheReason()
    {
        var listener = HangUpServer();
        try
        {
            var (service, log) = Sut(TimeSpan.FromHours(1));

            await service.ProbeOnceAsync(ConnectionStringOf(listener), TestContext.Current.CancellationToken);

            var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Contains("Slow PostgreSQL connection open to 127.0.0.1:", warning.Message, StringComparison.Ordinal);
            Assert.Contains("failure NpgsqlException", warning.Message, StringComparison.Ordinal);
            Assert.Contains("Npgsql open n/a ms", warning.Message, StringComparison.Ordinal);
            Assert.Contains("Background work none tracked", warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WithoutAConnectionString_TheProbeDoesNothing()
    {
        var (service, log) = Sut(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(log.Entries);
    }
}
