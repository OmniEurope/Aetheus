// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-1, slow requests: the commit of a telemetry call (log batch, probe results, OTLP ingestion, heartbeat) waited for
/// the host's busy disk. Inside a <see cref="RelaxedCommitDurability"/> scope the connection runs with
/// <c>synchronous_commit = off</c>; these tests read the setting from the real PostgreSQL the context talks to, and prove a
/// pooled connection does not carry it out of the scope. The effect on a request's duration under disk pressure is not
/// measured here (it needs the host's disk contended), only that the server applies and releases the setting.
/// </summary>
public sealed class RelaxedCommitDurabilityIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string Setting = "SELECT current_setting('synchronous_commit') AS \"Value\"";

    // One physical connection, so a setting left on it would be met by the next context.
    private DbContextOptions<AppDbContext> SingleConnectionOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                MaxPoolSize = 1,
                MinPoolSize = 0,
                // The test container is not configured as production is (its default is off): start every session durable.
                Options = "-c synchronous_commit=on",
                ApplicationName = "relaxed-commit-test"
            }.ConnectionString)
            .AddInterceptors(new RelaxedCommitConnectionInterceptor())
            .Options;

    private static async Task<string> ReadSettingAsync(AppDbContext db) =>
        (await db.Database.SqlQueryRaw<string>(Setting).ToListAsync(TestContext.Current.CancellationToken)).Single();

    [Fact]
    public async Task InsideTheScope_TheServerCommitsWithoutWaitingForTheDisk()
    {
        await using var db = new AppDbContext(SingleConnectionOptions());

        using (RelaxedCommitDurability.Begin())
            Assert.Equal("off", await ReadSettingAsync(db));
    }

    [Fact]
    public async Task AConnectionReturnedToThePoolAfterTheScope_IsDurableAgainForTheNextUser()
    {
        var options = SingleConnectionOptions();
        await using (var relaxed = new AppDbContext(options))
        using (RelaxedCommitDurability.Begin())
            Assert.Equal("off", await ReadSettingAsync(relaxed));

        // The pool holds one connection: this context gets the very one the scope used.
        await using var durable = new AppDbContext(options);
        Assert.Equal("on", await ReadSettingAsync(durable));
    }
}
