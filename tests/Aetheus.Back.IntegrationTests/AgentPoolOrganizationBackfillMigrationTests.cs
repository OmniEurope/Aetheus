// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-12: the migration that gives every agent pool an organization backfills the pools that existed before it,
/// on a real PostgreSQL, before its foreign key is added. The database is moved back to the migration before it
/// (its Down), legacy pools are inserted with the columns of that time, and the migration is applied again.
/// </summary>
public sealed class AgentPoolOrganizationBackfillMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string TargetMigration = "20261112084200_ScopeAgentPoolsToOrganizations";

    [Fact]
    public async Task Backfill_GivesEachLegacyPoolTheOrganizationOfMostOfItsServers_ElseTheDefaultOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        var migrations = db.Database.GetMigrations().ToList();
        var targetIndex = migrations.IndexOf(TargetMigration);
        Assert.True(targetIndex > 0, $"Could not locate migration {TargetMigration} after a predecessor.");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(cancellationToken: ct);

        // Organizations and servers are written with the current model on the current schema, before the
        // database is moved back: a column a later migration adds to them (R-27 MachineJobsJson) does not
        // exist once the schema is rewound. The pools are inserted afterwards without the OrganizationId
        // column this migration adds.
        var defaultOrganization = await db.Organizations.Where(o => o.Slug == "aetheus").Select(o => o.Id).SingleAsync(ct);
        var a = new Organization { Name = $"backfill-a-{Guid.NewGuid():N}", Slug = $"ba{Guid.NewGuid():N}"[..20] };
        var b = new Organization { Name = $"backfill-b-{Guid.NewGuid():N}", Slug = $"bb{Guid.NewGuid():N}"[..20] };
        db.Organizations.AddRange(a, b);
        await db.SaveChangesAsync(ct);
        var servers = new[] { a, a, b, b }.Select((organization, index) => new Server
        {
            Name = $"backfill-{index}-{Guid.NewGuid():N}",
            Hostname = $"backfill-{index}.test",
            OrganizationId = organization.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }).ToArray();
        db.Servers.AddRange(servers);
        await db.SaveChangesAsync(ct);
        await migrator.MigrateAsync(migrations[targetIndex - 1], ct);

        var majorityB = await InsertLegacyPoolAsync("majority-b", [servers[0].Id, servers[2].Id, servers[3].Id], ct);
        var tie = await InsertLegacyPoolAsync("tie", [servers[0].Id, servers[2].Id], ct);
        var empty = await InsertLegacyPoolAsync("empty", [], ct);

        Assert.Null(await Record.ExceptionAsync(() => migrator.MigrateAsync(cancellationToken: ct)));

        var owners = await db.AgentPools.AsNoTracking()
            .Where(pool => pool.Id == majorityB || pool.Id == tie || pool.Id == empty)
            .ToDictionaryAsync(pool => pool.Id, pool => pool.OrganizationId, ct);
        Assert.Equal(b.Id, owners[majorityB]);
        Assert.Equal(Math.Min(a.Id, b.Id), owners[tie]);
        Assert.Equal(defaultOrganization, owners[empty]);
        // Nothing is deleted: the link of the majority pool to A's server stays.
        Assert.Equal(3, await db.AgentPoolServers.CountAsync(link => link.AgentPoolId == majorityB, ct));
    }

    private async Task<int> InsertLegacyPoolAsync(string label, IReadOnlyList<int> serverIds, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        int poolId;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO "AgentPools" ("Name", "Description", "MaxConcurrency", "CreatedAt", "UpdatedAt")
                VALUES (@name, '', 1, NOW(), NOW())
                RETURNING "Id";
                """;
            command.Parameters.AddWithValue("name", $"legacy-{label}-{Guid.NewGuid():N}");
            poolId = Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var serverId in serverIds)
        {
            await using var link = connection.CreateCommand();
            link.CommandText = """INSERT INTO "AgentPoolServers" ("AgentPoolId", "ServerId") VALUES (@pool, @server);""";
            link.Parameters.AddWithValue("pool", poolId);
            link.Parameters.AddWithValue("server", serverId);
            await link.ExecuteNonQueryAsync(ct);
        }
        return poolId;
    }
}
