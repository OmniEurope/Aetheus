// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// ADR-060: the public identifier is a new way into a task, so the tenant barrier is proved on it through
/// the real HTTP pipeline. User B holds wildcard Server Read for organization B only (ADR-059): B's own
/// task is the positive control, A's task is refused exactly as by its integer address.
/// </summary>
public sealed class TaskUidTenantIsolationHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task UserOfOrganizationB_ReadsItsOwnTaskByUid_AndIsRefusedOrganizationATask()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await IntegrationUsers.CreateAsync(factory.Services, "task-uid-b",
            [new IntegrationUsers.Grant(ResourceType.Server, null, Permission.Read)],
            b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        var own = await client.GetAsync($"/api/tasks/{b.Uid}", ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var task = await own.Content.ReadFromJsonAsync<ServerTaskDto>(IntegrationJsonOptions.Default, ct);
        Assert.Equal(b.TaskId, task!.Id);
        Assert.Equal(b.Uid, task.Uid);
        Assert.Equal(1, task.Number);

        var foreign = await client.GetAsync($"/api/tasks/{a.Uid}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.DoesNotContain(a.Marker, await foreign.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // The same answer as the integer address: the new path reveals nothing the old one hid.
        var foreignByInteger = await client.GetAsync($"/api/tasks/{a.TaskId}", ct);
        Assert.Equal(foreign.StatusCode, foreignByInteger.StatusCode);

        var unknown = await client.GetAsync($"/api/tasks/{Guid.CreateVersion7()}", ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>The progress counts every organization's tasks, so a user of one of them never reads it.</summary>
    [Fact]
    public async Task BackfillProgress_IsForAdministratorsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var b = await SeedTenantAsync(factory, "progress");
        var userB = await IntegrationUsers.CreateAsync(factory.Services, "task-progress-b",
            [new IntegrationUsers.Grant(ResourceType.Server, null, Permission.Read)],
            b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        var response = await client.GetAsync("/api/admin/task-identity-backfill", ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record Tenant(int OrganizationId, int ServerId, int TaskId, Guid Uid, string Marker);

    private static async Task<Tenant> SeedTenantAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var marker = $"tenant-{label}-{Guid.NewGuid():N}";

        var organization = new Organization { Name = marker, Slug = $"t{label}{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var server = new Server
        {
            Name = $"{marker}-server",
            Hostname = $"{marker}.test",
            OrganizationId = organization.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        var task = new ServerTask { ServerId = server.Id, Name = marker, Command = "true" };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);

        return new Tenant(organization.Id, server.Id, task.Id, task.Uid!.Value, marker);
    }
}
