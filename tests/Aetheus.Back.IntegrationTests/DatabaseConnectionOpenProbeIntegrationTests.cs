// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Middleware;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-6 on a real PostgreSQL: the measurement of a brand-new connection returns every phase, in an order that
/// makes sense (the Npgsql open includes its own resolution and connect, so it is at least the TCP connect).
/// The unit tests cover the failure path with a socket that hangs up; only a real server proves the success path.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseConnectionOpenProbeIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OpeningAFreshConnection_ReturnsEveryPhaseAndNoFailure()
    {
        var timings = await DatabaseConnectionOpenMeasurement.MeasureAsync(
            fixture.ConnectionString, TimeProvider.System, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Null(timings.Failure);
        Assert.NotNull(timings.Dns);
        Assert.NotNull(timings.TcpConnect);
        Assert.NotNull(timings.ProtocolOpen);
        Assert.NotNull(timings.FirstQuery);
        Assert.True(timings.ProtocolOpen >= timings.TcpConnect, "the open includes the TCP connect it performs itself");
        Assert.True(timings.Total >= timings.ProtocolOpen);
    }
}
