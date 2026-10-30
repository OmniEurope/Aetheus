// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// RBAC audit regression guard (ADR-059): a Contributor of organization B, alone or also Reader of
/// organization A, sends every write the audit touched at A's resources and gets only 403 or 404,
/// never a 400 (a validation answer would mean the authorization was never reached) nor a 2xx; A's
/// rows are all still there afterwards.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CrossOrganizationWriteMatrixIntegrationTests(PostgresFixture fixture)
{
    private static readonly HttpStatusCode[] Refused = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContributorOfB_GetsOnlyForbiddenOrNotFound_OnEveryWriteToAResourceOfA(bool alsoReaderOfA)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var victim = await RbacAuditSeed.CreateUserAsync(factory.Services, "victim", (RoleNames.Reader, a.OrganizationId));
        (string, int)[] holdings = alsoReaderOfA
            ? [(RoleNames.Contributor, b.OrganizationId), (RoleNames.Reader, a.OrganizationId)]
            : [(RoleNames.Contributor, b.OrganizationId)];
        var caller = await RbacAuditSeed.CreateUserAsync(factory.Services, "matrix", holdings);
        var (backupId, appId, victimMemberId) = await SeedOperationsAsync(factory.Services, a, victim.Id);
        using var client = await IntegrationUsers.LoginClientAsync(factory, caller.Username);

        var writes = new (string Label, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("vault delete", () => client.DeleteAsync($"/api/vaults/{a.VaultId}", ct)),
            ("vault secret add", () => client.PostAsJsonAsync($"/api/vaults/{a.VaultId}/secrets", new { key = "INJECTED", value = "x" }, ct)),
            ("library delete", () => client.DeleteAsync($"/api/variable-libraries/{a.LibraryId}", ct)),
            ("library entry add", () => client.PostAsJsonAsync($"/api/variable-libraries/{a.LibraryId}/entries", new CreateVariableEntryRequest { Key = "INJECTED", Value = "x" }, ct)),
            ("library port allocate", () => client.PostAsJsonAsync($"/api/variable-libraries/{a.LibraryId}/ports/allocate", new AllocateLibraryPortsRequest { ServerId = a.ServerId, Keys = ["PORT_X"] }, ct)),
            ("own library port on A's server", () => client.PostAsJsonAsync($"/api/variable-libraries/{b.LibraryId}/ports/allocate", new AllocateLibraryPortsRequest { ServerId = a.ServerId, Keys = ["PORT_X"] }, ct)),
            ("environment delete", () => client.DeleteAsync($"/api/environments/{a.EnvironmentId}", ct)),
            ("environment duplicate into B", () => client.PostAsJsonAsync($"/api/environments/{a.EnvironmentId}/duplicate", new DuplicateEnvironmentRequest { TargetProjectId = b.ProjectId }, ct)),
            ("own environment linked to A's project server", () => client.PostAsync($"/api/environments/{b.EnvironmentId}/project-servers/{a.ProjectServerId}", null, ct)),
            ("pipeline delete", () => client.DeleteAsync($"/api/pipelines/{a.PipelineId}", ct)),
            ("pipeline run", () => client.PostAsJsonAsync($"/api/pipelines/{a.PipelineId}/run", new PipelineRunRequest(), ct)),
            ("server delete", () => client.DeleteAsync($"/api/servers/{a.ServerId}", ct)),
            ("project delete", () => client.DeleteAsync($"/api/projects/{a.ProjectId}", ct)),
            ("project server unlink", () => client.DeleteAsync($"/api/projects/{a.ProjectId}/servers/{a.ProjectServerId}", ct)),
            ("backup delete", () => client.DeleteAsync($"/api/backups/{backupId}", ct)),
            ("backup run", () => client.PostAsync($"/api/backups/{backupId}/run", null, ct)),
            ("monitored app delete", () => client.DeleteAsync($"/api/appmonitoring/apps/{appId}", ct)),
            ("monitored app ingest key", () => client.PostAsync($"/api/appmonitoring/apps/{appId}/ingest-key", null, ct)),
            ("own monitored app probed from A's server", () => client.PostAsJsonAsync($"/api/appmonitoring/projects/{b.ProjectId}/apps", new CreateMonitoredAppRequest { Name = $"{b.Marker}-cross", ProjectId = b.ProjectId, ServerId = a.ServerId, ProbeUrl = "http://localhost/health" }, ct)),
            ("organization delete", () => client.DeleteAsync($"/api/organizations/{a.OrganizationId}", ct)),
            ("organization member add", () => client.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members", new AddOrganizationMemberRequest { UserId = caller.Id, Role = OrganizationRole.Owner }, ct)),
            ("organization member promote", () => client.PutAsJsonAsync($"/api/organizations/{a.OrganizationId}/members/{victimMemberId}", new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, ct)),
            ("organization member remove", () => client.DeleteAsync($"/api/organizations/{a.OrganizationId}/members/{victimMemberId}", ct)),
        };

        var leaks = new List<string>();
        foreach (var (label, send) in writes)
        {
            using var response = await send();
            if (!Refused.Contains(response.StatusCode))
                leaks.Add($"{label}: {(int)response.StatusCode}");
        }
        Assert.Empty(leaks);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Vaults.AnyAsync(row => row.Id == a.VaultId, ct));
        Assert.False(await db.VaultSecrets.AnyAsync(row => row.VaultId == a.VaultId && row.Key == "INJECTED", ct));
        Assert.True(await db.VariableLibraries.AnyAsync(row => row.Id == a.LibraryId, ct));
        Assert.False(await db.VariableLibraryEntries.AnyAsync(row => row.VariableLibraryId == a.LibraryId && row.Key != RbacAuditSeed.SharedKey, ct));
        Assert.True(await db.Environments.AnyAsync(row => row.Id == a.EnvironmentId, ct));
        Assert.Equal(1, await db.Environments.CountAsync(row => row.ProjectId == b.ProjectId, ct));
        Assert.True(await db.Pipelines.AnyAsync(row => row.Id == a.PipelineId, ct));
        Assert.False(await db.PipelineRuns.AnyAsync(row => row.PipelineId == a.PipelineId, ct));
        Assert.True(await db.Servers.AnyAsync(row => row.Id == a.ServerId && row.DeletedAt == null, ct));
        Assert.True(await db.Projects.AnyAsync(row => row.Id == a.ProjectId, ct));
        Assert.True(await db.ProjectServers.AnyAsync(row => row.Id == a.ProjectServerId, ct));
        Assert.False(await db.ServerPortReservations.AnyAsync(row => row.ServerId == a.ServerId, ct));
        Assert.True(await db.BackupPolicies.AnyAsync(row => row.Id == backupId, ct));
        Assert.False(await db.BackupRuns.AnyAsync(row => row.BackupPolicyId == backupId, ct));
        Assert.True(await db.MonitoredApps.AnyAsync(row => row.Id == appId && row.IngestKeyHash == null, ct));
        Assert.False(await db.MonitoredApps.AnyAsync(row => row.ServerId == a.ServerId, ct));
        Assert.True(await db.Organizations.AnyAsync(row => row.Id == a.OrganizationId, ct));
        Assert.False(await db.OrganizationMembers.AnyAsync(row => row.OrganizationId == a.OrganizationId && row.UserId == caller.Id && row.Role != OrganizationRole.Member, ct));
        Assert.Equal(OrganizationRole.Member, (await db.OrganizationMembers.SingleAsync(row => row.Id == victimMemberId, ct)).Role);
    }

    private static async Task<(int BackupId, int AppId, int VictimMemberId)> SeedOperationsAsync(
        IServiceProvider services, RbacAuditSeed.Tenant a, int victimId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var backup = new BackupPolicy
        {
            Name = $"{a.Marker}-backup",
            ProjectId = a.ProjectId,
            ServerId = a.ServerId,
            ScheduleCron = "0 3 * * *",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var app = new MonitoredApp { Name = $"{a.Marker}-app", ProjectId = a.ProjectId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.AddRange(backup, app);
        await db.SaveChangesAsync(ct);
        var memberId = await db.OrganizationMembers
            .Where(row => row.OrganizationId == a.OrganizationId && row.UserId == victimId)
            .Select(row => row.Id)
            .SingleAsync(ct);
        return (backup.Id, app.Id, memberId);
    }
}
