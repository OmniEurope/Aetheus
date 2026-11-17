// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-8 (option B): <c>api/servers/{id}/reboot-schedule</c> through the real HTTP pipeline, JWT included, on real
/// PostgreSQL. Only a server Admin creates, changes or removes a reboot; a reader sees it; every change is audited; the
/// planner lists the reboot beside the pipelines, read only.
/// </summary>
public sealed class ServerRebootScheduleHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly SaveServerRebootScheduleRequest Daily = new() { Hour = 2, Minute = 5, TimeZoneId = "Europe/Paris", Enabled = true };

    [Fact]
    public async Task OnlyAServerAdmin_ChangesTheReboot_AReaderSeesIt_AndEveryChangeIsAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organizationId, serverId) = await SeedServerAsync(factory);
        var pipelineRead = new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read);
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "reboot-reader",
            [pipelineRead, new IntegrationUsers.Grant(ResourceType.Server, serverId, Permission.Read)], organizationId);
        var writer = await IntegrationUsers.CreateAsync(factory.Services, "reboot-writer",
            [new IntegrationUsers.Grant(ResourceType.Server, serverId, Permission.Write)], organizationId);
        var administrator = await IntegrationUsers.CreateAsync(factory.Services, "reboot-admin",
            [new IntegrationUsers.Grant(ResourceType.Server, serverId, Permission.Admin)], organizationId);
        using var readerClient = await IntegrationUsers.LoginClientAsync(factory, reader.Username);
        using var writerClient = await IntegrationUsers.LoginClientAsync(factory, writer.Username);
        using var adminClient = await IntegrationUsers.LoginClientAsync(factory, administrator.Username);
        var url = $"/api/servers/{serverId}/reboot-schedule";

        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync(url, Daily, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writerClient.PutAsJsonAsync(url, Daily, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await readerClient.GetAsync(url, ct)).StatusCode);

        var saved = await adminClient.PutAsJsonAsync(url, Daily, ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(ct));
        var seen = await (await readerClient.GetAsync(url, ct)).Content.ReadFromJsonAsync<ServerRebootScheduleDto>(IntegrationJsonOptions.Default, ct);
        Assert.NotNull(seen);
        Assert.Equal("Europe/Paris", seen.TimeZoneId);
        Assert.False(seen.CanManage);
        Assert.Equal(1, seen.Version);

        var unknownZone = await adminClient.PutAsJsonAsync(url, Daily with { TimeZoneId = "Mars/Olympus" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknownZone.StatusCode);

        // The planner lists it beside the pipelines, read only, with its zone.
        var plan = await readerClient.GetFromJsonAsync<SchedulePlanDto>(
            $"/api/pipelines/schedule-plan?organizationId={organizationId}&from=2026-11-08T00:00:00Z&to=2026-11-09T00:00:00Z",
            IntegrationJsonOptions.Default, ct);
        var row = Assert.Single(plan!.ServerActions, action => action.Kind == ServerActionKind.ServerReboot);
        Assert.False(row.Movable);
        Assert.Equal("Europe/Paris", row.Reboot!.TimeZoneId);
        Assert.Equal(new DateTime(2026, 11, 8, 1, 5, 0, DateTimeKind.Utc), Assert.Single(row.Occurrences).StartUtc);

        Assert.Equal(HttpStatusCode.Forbidden, (await writerClient.DeleteAsync(url, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.DeleteAsync(url, ct)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audited = await db.AuditLogs.AsNoTracking()
            .Where(log => log.EntityType == "ServerReboot" && log.EntityId == serverId)
            .Select(log => log.Action).ToListAsync(ct);
        Assert.Contains("ServerRebootScheduleSaved", audited);
        Assert.Contains("ServerRebootScheduleDeleted", audited);
        Assert.False(await db.ServerRebootSchedules.AnyAsync(schedule => schedule.ServerId == serverId, ct));
    }

    private static async Task<(int OrganizationId, int ServerId)> SeedServerAsync(AetheusWebApplicationFactory factory)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"reboot-{Guid.NewGuid():N}", Slug = $"r{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var name = $"reboot-{Guid.NewGuid():N}"[..30];
        var server = new Server { Name = name, Hostname = name, Status = ServerStatus.Online, OrganizationId = organization.Id };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        return (organization.Id, server.Id);
    }
}
