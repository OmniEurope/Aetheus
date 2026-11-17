// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Data.Migrations;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.PortRegistry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The data migrations of the BACK-002 and BACK-008 follow-ups. The SQL under test is the migration's own: its
/// <c>Up</c> is invoked and the <see cref="SqlOperation"/> it emits is executed on real PostgreSQL (a template
/// database that already went through every migration), after seeding the stale rows by hand. Each test also
/// runs the SQL a second time to prove it is idempotent, a no-op once nothing is left to fix.
/// Not covered: the migrations running against a production-sized table (volume and locking).
/// </summary>
public sealed class PortAndReleaseDataMigrationIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReleaseMigration_RealignsAnOrphan_AndLeavesTheOthers()
    {
        await ResetAndMigrateAsync();
        int orphan, alreadyThere, foreign, noCreator, versionTaken, liveBlocked;
        int destinationId, oldId;
        await using (var db = NewContext())
        {
            var org = await SeedOrganizationAsync(db);
            var oldProject = NewProject(org, "old");
            var destination = NewProject(org, "destination");
            db.Projects.AddRange(oldProject, destination);
            await db.SaveChangesAsync(Ct);
            var moved = NewPipeline(destination.Id, "moved");
            var other = NewPipeline(oldProject.Id, "other");
            db.Pipelines.AddRange(moved, other);
            await db.SaveChangesAsync(Ct);
            var movedRun = new PipelineRun { PipelineId = moved.Id, Status = PipelineStatus.Success };
            var otherRun = new PipelineRun { PipelineId = other.Id, Status = PipelineStatus.Success };
            db.PipelineRuns.AddRange(movedRun, otherRun);
            await db.SaveChangesAsync(Ct);

            var rows = new Dictionary<string, Release>
            {
                // Published by the moved pipeline, still in the old project: realigned.
                ["orphan"] = NewRelease(oldProject.Id, "1.0.0", movedRun.Id),
                // Already on the pipeline's project: untouched.
                ["alreadyThere"] = NewRelease(destination.Id, "0.9.0", movedRun.Id),
                // Published by another pipeline, then a deployment of the moved one overwrote PipelineRunId: not its release.
                ["foreign"] = NewRelease(oldProject.Id, "1.1.0", otherRun.Id, latestRunId: movedRun.Id),
                // No creating run at all (PipelineRunId alone does not say who published it): untouched.
                ["noCreator"] = NewRelease(oldProject.Id, "1.2.0", null, latestRunId: movedRun.Id),
                // The destination already holds this version: stays.
                ["versionTaken"] = NewRelease(oldProject.Id, "2.0.0", movedRun.Id),
                ["holdsVersion"] = NewRelease(destination.Id, "2.0.0", null),
                // Live in the old project while the destination already has a live release: stays.
                ["liveBlocked"] = NewRelease(oldProject.Id, "3.0.0", movedRun.Id, status: ReleaseStatus.Deployed),
                ["liveHolder"] = NewRelease(destination.Id, "2.9.0", null, status: ReleaseStatus.Deployed)
            };
            db.Releases.AddRange(rows.Values);
            await db.SaveChangesAsync(Ct);
            (orphan, alreadyThere, foreign, noCreator, versionTaken, liveBlocked) =
                (rows["orphan"].Id, rows["alreadyThere"].Id, rows["foreign"].Id, rows["noCreator"].Id,
                 rows["versionTaken"].Id, rows["liveBlocked"].Id);
            (destinationId, oldId) = (destination.Id, oldProject.Id);
        }

        await RunMigrationSqlAsync<RealignMovedPipelineReleaseProjects>();
        await RunMigrationSqlAsync<RealignMovedPipelineReleaseProjects>();

        await using var verify = NewContext();
        var stored = await verify.Releases.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.ProjectId, Ct);
        Assert.Equal(destinationId, stored[orphan]);
        Assert.Equal(destinationId, stored[alreadyThere]);
        Assert.Equal(oldId, stored[foreign]);
        Assert.Equal(oldId, stored[noCreator]);
        Assert.Equal(oldId, stored[versionTaken]);
        Assert.Equal(oldId, stored[liveBlocked]);
    }

    [Fact]
    public async Task PortMigration_KeepsTheFirstClaim_AndDeletesOnlyTheConflictingDuplicate()
    {
        await ResetAndMigrateAsync();
        Dictionary<string, int> ids;
        await using (var db = NewContext())
        {
            var org = await SeedOrganizationAsync(db);
            var server = new Server { Name = "dup-ports", Hostname = "dup-ports-host", OrganizationId = org, Status = ServerStatus.Online };
            var otherServer = new Server { Name = "dup-ports-2", Hostname = "dup-ports-host-2", OrganizationId = org, Status = ServerStatus.Online };
            db.Servers.AddRange(server, otherServer);
            await db.SaveChangesAsync(Ct);

            var rows = new Dictionary<string, ServerPortReservation>
            {
                // Declared first, Manual of another owner later: the Manual one is the duplicate.
                ["declaredFirst"] = Claim(server.Id, 15001, PortReservationSource.Declared, "project:1", T0),
                ["manualLater"] = Claim(server.Id, 15001, PortReservationSource.Manual, "manual:crm", T0.AddSeconds(1)),
                // Manual first, Declared of another owner later: the Declared one is the duplicate.
                ["manualFirst"] = Claim(server.Id, 15002, PortReservationSource.Manual, "manual:crm", T0),
                ["declaredLater"] = Claim(server.Id, 15002, PortReservationSource.Declared, "project:2", T0.AddSeconds(1)),
                // Same instant: Declared wins over Manual.
                ["tieDeclared"] = Claim(server.Id, 15003, PortReservationSource.Declared, "project:3", T0),
                ["tieManual"] = Claim(server.Id, 15003, PortReservationSource.Manual, "manual:legacy", T0),
                // Same owner on both sources is not a conflict for the code: both stay.
                ["sameOwnerDeclared"] = Claim(server.Id, 15004, PortReservationSource.Declared, "manual:app", T0),
                ["sameOwnerManual"] = Claim(server.Id, 15004, PortReservationSource.Manual, "manual:app", T0.AddSeconds(1)),
                // A lone claim, an observation next to a claim, a UDP pair and the same port on another server: untouched.
                ["alone"] = Claim(server.Id, 15005, PortReservationSource.Declared, "project:5", T0),
                ["claimWithSighting"] = Claim(server.Id, 15006, PortReservationSource.Declared, "project:6", T0),
                ["sighting"] = Claim(server.Id, 15006, PortReservationSource.Observed, "observed", T0.AddSeconds(5)),
                ["udpDeclared"] = Claim(server.Id, 15007, PortReservationSource.Declared, "project:7", T0, "udp"),
                ["udpManual"] = Claim(server.Id, 15007, PortReservationSource.Manual, "manual:dns", T0.AddSeconds(1), "udp"),
                ["otherServerDeclared"] = Claim(otherServer.Id, 15001, PortReservationSource.Declared, "project:8", T0),
                ["otherServerManual"] = Claim(otherServer.Id, 15001, PortReservationSource.Manual, "manual:crm", T0.AddSeconds(1))
            };
            db.ServerPortReservations.AddRange(rows.Values);
            await db.SaveChangesAsync(Ct);
            ids = rows.ToDictionary(pair => pair.Key, pair => pair.Value.Id);
        }

        await RunMigrationSqlAsync<RemoveConflictingPortClaimDuplicates>();

        var surviving = await SurvivingIdsAsync();
        string[] deleted = ["manualLater", "declaredLater", "tieManual", "otherServerManual"];
        foreach (var (name, id) in ids)
            Assert.True(deleted.Contains(name) != surviving.Contains(id), $"{name}: expected {(deleted.Contains(name) ? "deleted" : "kept")}.");

        // A second run finds nothing left: the surviving rows are exactly the same.
        await RunMigrationSqlAsync<RemoveConflictingPortClaimDuplicates>();
        Assert.Equal(surviving.Order(), (await SurvivingIdsAsync()).Order());
    }

    [Fact]
    public async Task PortMigration_IsANoOp_WhenThereAreNoDuplicates()
    {
        await ResetAndMigrateAsync();
        await using (var db = NewContext())
        {
            var org = await SeedOrganizationAsync(db);
            var server = new Server { Name = "clean-ports", Hostname = "clean-ports-host", OrganizationId = org, Status = ServerStatus.Online };
            db.Servers.Add(server);
            await db.SaveChangesAsync(Ct);
            db.ServerPortReservations.AddRange(
                Claim(server.Id, 15100, PortReservationSource.Declared, "project:1", T0),
                Claim(server.Id, 15101, PortReservationSource.Manual, "manual:crm", T0),
                Claim(server.Id, 15101, PortReservationSource.Observed, "observed", T0));
            await db.SaveChangesAsync(Ct);
        }

        var before = await SurvivingIdsAsync();
        await RunMigrationSqlAsync<RemoveConflictingPortClaimDuplicates>();
        Assert.Equal(3, before.Count);
        Assert.Equal(before.Order(), (await SurvivingIdsAsync()).Order());
    }

    private async Task<List<int>> SurvivingIdsAsync()
    {
        await using var verify = NewContext();
        return await verify.ServerPortReservations.AsNoTracking().Select(row => row.Id).ToListAsync(Ct);
    }

    /// <summary>Invokes the migration's <c>Up</c> and executes the SQL it emitted.</summary>
    private async Task RunMigrationSqlAsync<TMigration>() where TMigration : Migration, new()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(TMigration).GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new TMigration(), [builder]);
        var statements = builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql).ToList();
        Assert.Single(statements);

        await using var db = NewContext();
#pragma warning disable EF1002 // The SQL is the migration's own constant text, with no interpolated value.
        await db.Database.ExecuteSqlRawAsync(statements[0], Ct);
#pragma warning restore EF1002
    }

    private static async Task<int> SeedOrganizationAsync(AppDbContext db)
    {
        var organization = new Organization { Name = $"data-migration-{Guid.NewGuid():N}", Slug = $"dm-{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        return organization.Id;
    }

    private static Project NewProject(int organizationId, string name) =>
        new() { Name = $"{name}-{Guid.NewGuid():N}", OrganizationId = organizationId };

    private static Pipeline NewPipeline(int projectId, string name) => new()
    {
        Name = $"{name}-{Guid.NewGuid():N}",
        ProjectId = projectId,
        YamlDefinition = RbacAuditSeed.PipelineYaml,
        CreatedByUsername = "admin"
    };

    private static Release NewRelease(
        int projectId, string version, int? createdByRunId, ReleaseStatus status = ReleaseStatus.Published,
        int? latestRunId = null) => new()
        {
            ProjectId = projectId,
            Version = version,
            BranchName = "main",
            Status = status,
            DetectedAt = T0,
            PublishedAt = T0,
            PipelineRunId = latestRunId ?? createdByRunId,
            CreatedByPipelineRunId = createdByRunId
        };

    private static ServerPortReservation Claim(
        int serverId, int port, PortReservationSource source, string ownerKey, DateTime declaredAt, string protocol = "tcp") => new()
        {
            ServerId = serverId,
            Port = port,
            Protocol = protocol,
            OwnerKey = ownerKey,
            OwnerLabel = ownerKey,
            Source = source,
            DeclaredAt = declaredAt,
            UpdatedAt = declaredAt
        };
}
