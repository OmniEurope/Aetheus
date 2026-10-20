// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>PostgreSQL tests verify translation of retirement filters, environment-link navigation
/// and watchdog exclusion.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ServerRetirementIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task RetiredServer_IsHiddenWithItsLinks_ButItsQueuedWorkStillAgesOut()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int serverId, environmentId;

        await using (var db = NewContext())
        {
            var org = new Organization { Name = "Org retire", Slug = "retire", Description = "d" };
            db.Organizations.Add(org);
            await db.SaveChangesAsync(ct);
            var server = new Server { Name = "vps-retired", Hostname = "vps-retired", OrganizationId = org.Id };
            var environment = new Environment { Name = "Production retire" };
            db.Servers.Add(server);
            db.Environments.Add(environment);
            await db.SaveChangesAsync(ct);
            db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = environment.Id, ServerId = server.Id });
            db.Tasks.Add(new ServerTask { ServerId = server.Id, Name = "queued", Status = TaskExecutionStatus.Pending });
            await db.SaveChangesAsync(ct);
            (serverId, environmentId) = (server.Id, environment.Id);
        }

        await using (var db = NewContext())
        {
            var repo = new ServerRetirementRepository(db);
            await repo.RetireAsync((await repo.FindServerIncludingRetiredAsync(serverId, ct))!, DateTime.UtcNow, ct);
        }

        await using var verify = NewContext();
        Assert.False(await verify.Servers.AnyAsync(s => s.Id == serverId, ct));
        Assert.False(await verify.EnvironmentServers.AnyAsync(link => link.EnvironmentId == environmentId, ct));
        Assert.True(await verify.EnvironmentServers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired])
            .AnyAsync(link => link.EnvironmentId == environmentId && link.ServerId == serverId, ct));
        var stale = await new TaskRepository(verify, TimeProvider.System).GetStalePendingTasksAsync(TimeSpan.Zero, ct);
        Assert.Contains(stale, task => task.ServerId == serverId);
    }
}
