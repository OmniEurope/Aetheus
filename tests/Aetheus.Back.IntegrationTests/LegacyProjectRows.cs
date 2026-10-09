// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// A project row written on a database held at an older migration. Through the entity, EF inserts every
/// column today's model has, and PostgreSQL answers 42703 for any column added since that migration
/// point: <c>Projects.ReleaseCounter</c> (20261004113420_AddProjectReleaseNumber) broke three migration
/// tests that way. Naming only the columns a project has always required keeps those tests about their
/// migration instead of about the current model, as the Releases inserts of MigrationRegressionTests do.
/// </summary>
internal static class LegacyProjectRows
{
    public static async Task<int> InsertAsync(
        AppDbContext db, string name, int organizationId, CancellationToken ct, string description = "")
    {
        var now = DateTime.UtcNow;
        var ids = await db.Database.SqlQueryRaw<int>(
                """
                INSERT INTO "Projects" ("Name", "Description", "Tags", "Status", "OrganizationId", "CreatedAt", "UpdatedAt")
                VALUES (@name, @description, '', 0, @organizationId, @now, @now)
                RETURNING "Id" AS "Value"
                """,
                new NpgsqlParameter("name", name),
                new NpgsqlParameter("description", description),
                new NpgsqlParameter("organizationId", organizationId),
                new NpgsqlParameter("now", now))
            .ToListAsync(ct);
        return ids.Single();
    }
}
