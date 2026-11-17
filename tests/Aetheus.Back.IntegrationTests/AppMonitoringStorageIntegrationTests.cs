// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

public sealed class AppMonitoringStorageIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task TelemetryStorageBytes_IncludesEveryTelemetryTable()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var repository = new AppMonitoringRepository(db);

        var measured = await repository.GetTelemetryStorageBytesAsync(TestContext.Current.CancellationToken);
        var expected = await db.Database.SqlQueryRaw<long>("""
                SELECT COALESCE(SUM(pg_total_relation_size(quote_ident(schemaname) || '.' || quote_ident(tablename))), 0)::bigint AS "Value"
                FROM pg_tables
                WHERE schemaname = current_schema()
                  AND tablename IN ('AppMetricSamples', 'AppMetricHourly', 'AppLogEntries', 'AppErrorEvents', 'AppHealthSamples', 'AppHealthHourly')
                """)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.True(expected > 0);
        Assert.Equal(expected, measured);
    }
}
