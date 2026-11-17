// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Shared.Components.SystemLogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>R-6: the administrator view of the table volumes, over the real HTTP pipeline and PostgreSQL
/// (Testcontainers): estimated rows, size and the key against the limit of its type.</summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class DatabaseTableVolumeIntegrationTests(ApiSmokeFixture fixture)
{
    [Fact]
    public async Task Admin_GetsTheRealVolumesOfAProbeTableAndOfTheSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        const string probe = "vol_probe_int";
        const string bigProbe = "vol_probe_big";
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Test setup, not production code: two tables with identity keys and known contents (the
        // collection runs one test at a time, so fixed names are safe).
        await RunAsync(db, connection => new NpgsqlCommand(DropProbes, connection), ct);
        await RunAsync(db, connection => new NpgsqlCommand(CreateProbes, connection), ct);
        try
        {
            using var admin = fixture.CreateAdminClient();

            var tables = await admin.GetFromJsonAsync<List<DatabaseTableVolumeDto>>("/api/admin/database-tables", IntegrationJsonOptions.Default, ct);

            Assert.NotNull(tables);
            var table = Assert.Single(tables, t => t.Name == probe);
            Assert.Equal(5000, table.EstimatedRows);
            Assert.True(table.TotalBytes > 5000 * 200, $"size {table.TotalBytes}");
            Assert.Equal("integer", table.KeyType);
            Assert.Equal(5000, table.MaxKey);
            Assert.Equal(int.MaxValue, table.KeyLimit);
            Assert.InRange(table.KeyLimitPercent!.Value, 5000d / int.MaxValue * 100 * 0.999, 5000d / int.MaxValue * 100 * 1.001);

            var big = Assert.Single(tables, t => t.Name == bigProbe);
            Assert.Equal("bigint", big.KeyType);
            Assert.Equal(1, big.MaxKey);
            Assert.Equal(long.MaxValue, big.KeyLimit);

            // The real application tables are listed too, and an integer key of the schema is read.
            var projects = Assert.Single(tables, t => t.Name == "Projects");
            Assert.Equal("integer", projects.KeyType);
            Assert.True(projects.MaxKey >= 1);
            // Highest percentage first.
            var percents = tables.Select(t => t.KeyLimitPercent ?? -1).ToList();
            Assert.Equal(percents.OrderByDescending(p => p), percents);
        }
        finally
        {
            await RunAsync(db, connection => new NpgsqlCommand(DropProbes, connection), ct);
        }
    }

    private const string DropProbes = "DROP TABLE IF EXISTS vol_probe_int; DROP TABLE IF EXISTS vol_probe_big;";

    private const string CreateProbes =
        "CREATE TABLE vol_probe_int (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, payload text NOT NULL); "
        + "INSERT INTO vol_probe_int (payload) SELECT repeat('x', 200) FROM generate_series(1, 5000); ANALYZE vol_probe_int; "
        + "CREATE TABLE vol_probe_big (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY); "
        + "INSERT INTO vol_probe_big DEFAULT VALUES; ANALYZE vol_probe_big;";

    private static async Task RunAsync(AppDbContext db, Func<NpgsqlConnection, NpgsqlCommand> make, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var command = make(connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Reader_IsRefused_WithForbidden()
    {
        using var reader = await fixture.CreateReaderClientAsync();

        using var response = await reader.GetAsync("/api/admin/database-tables", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
