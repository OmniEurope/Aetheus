// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.AiTasks;
using Aetheus.Shared.Components.AppBackups;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-8, the AI tasks and the backups of the build planner, through the real HTTP pipeline, JWT and RBAC, on real
/// PostgreSQL: a source is listed only for an owner (project or server) the caller reads, movable only with the write
/// right its own endpoint asks for (which refuses the write again), and never crosses the organization.
/// </summary>
public sealed class ScheduledActionsPlanHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        int OrganizationA, int OrganizationB, int ProjectA1, int ProjectA2, int ProjectB, int ServerA, int ServerB,
        int AiOfProjectA1, int AiOfServerA, int AiOfProjectB, int BackupA1, int BackupA2, int BackupB);

    private static string Url(int organizationId) =>
        $"/api/pipelines/schedule-plan?organizationId={organizationId}&from={Day:yyyy-MM-ddTHH:mm:ssZ}&to={Day.AddDays(1):yyyy-MM-ddTHH:mm:ssZ}";

    [Fact]
    public async Task AiTasksAndBackups_FollowTheRightsOfTheirOwners_AndNeverCrossTheOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var world = await SeedAsync(factory);
        var none = await IntegrationUsers.CreateAsync(factory.Services, "act-none",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], world.OrganizationA);
        // Read on project A1, plus the strongest right on rights that belong to organization B: they reach nothing here.
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "act-reader",
            [new IntegrationUsers.Grant(ResourceType.Project, world.ProjectA1, Permission.Read),
             new IntegrationUsers.Grant(ResourceType.Project, world.ProjectB, Permission.Admin),
             new IntegrationUsers.Grant(ResourceType.Server, world.ServerB, Permission.Admin)], world.OrganizationA);
        var projectWriter = await IntegrationUsers.CreateAsync(factory.Services, "act-project-writer",
            [new IntegrationUsers.Grant(ResourceType.Project, world.ProjectA1, Permission.Write)], world.OrganizationA);
        var serverWriter = await IntegrationUsers.CreateAsync(factory.Services, "act-server-writer",
            [new IntegrationUsers.Grant(ResourceType.Server, world.ServerA, Permission.Write)], world.OrganizationA);
        using var noneClient = await IntegrationUsers.LoginClientAsync(factory, none.Username);
        using var readerClient = await IntegrationUsers.LoginClientAsync(factory, reader.Username);
        using var projectWriterClient = await IntegrationUsers.LoginClientAsync(factory, projectWriter.Username);
        using var serverWriterClient = await IntegrationUsers.LoginClientAsync(factory, serverWriter.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        Assert.Empty((await ReadPlanAsync(noneClient, world.OrganizationA)).ServerActions);

        // Read without write: the AI task and the backup of project A1 are listed, read only, and both endpoints refuse.
        var read = (await ReadPlanAsync(readerClient, world.OrganizationA)).ServerActions;
        Assert.Equal([$"ai:{world.AiOfProjectA1}", $"backup:{world.BackupA1}"], read.Select(action => action.Id).Order());
        Assert.All(read, action => Assert.False(action.Movable));
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync(
            $"/api/ai/tasks/{world.AiOfProjectA1}/schedule", new SetAiTaskScheduleRequest { Schedule = "0 0 2 * * *" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync(
            $"/api/backups/{world.BackupA1}/schedule", new SetBackupScheduleRequest { ScheduleCron = "0 5 * * *" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync(Url(world.OrganizationB), ct)).StatusCode);

        // Write on the project: its AI task and backup move, with the write checked again by each endpoint; the server's
        // AI task and the other project's backup stay out of sight.
        var written = (await ReadPlanAsync(projectWriterClient, world.OrganizationA)).ServerActions;
        Assert.Equal([$"ai:{world.AiOfProjectA1}", $"backup:{world.BackupA1}"], written.Select(action => action.Id).Order());
        Assert.All(written, action => Assert.True(action.Movable));
        var aiMoved = await projectWriterClient.PutAsJsonAsync(
            $"/api/ai/tasks/{world.AiOfProjectA1}/schedule", new SetAiTaskScheduleRequest { Schedule = "0 0 2 * * *" }, ct);
        Assert.True(aiMoved.IsSuccessStatusCode, await aiMoved.Content.ReadAsStringAsync(ct));
        var backupMoved = await projectWriterClient.PutAsJsonAsync(
            $"/api/backups/{world.BackupA1}/schedule", new SetBackupScheduleRequest { ScheduleCron = "0 5 * * *" }, ct);
        Assert.True(backupMoved.IsSuccessStatusCode, await backupMoved.Content.ReadAsStringAsync(ct));
        var afterMove = (await ReadPlanAsync(projectWriterClient, world.OrganizationA)).ServerActions.ToDictionary(action => action.Kind);
        Assert.Equal(2, afterMove[ServerActionKind.AiTask].Hour);
        Assert.Equal("0 0 2 * * *", afterMove[ServerActionKind.AiTask].CronExpression);
        Assert.Equal(5, afterMove[ServerActionKind.AppBackup].Hour);
        Assert.Equal("0 5 * * *", afterMove[ServerActionKind.AppBackup].CronExpression);
        // Another project's backup, an expression nothing can fire, and a task of a project the caller only reads.
        Assert.Equal(HttpStatusCode.Forbidden, (await projectWriterClient.PutAsJsonAsync(
            $"/api/backups/{world.BackupA2}/schedule", new SetBackupScheduleRequest { ScheduleCron = "0 5 * * *" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await projectWriterClient.PutAsJsonAsync(
            $"/api/ai/tasks/{world.AiOfProjectA1}/schedule", new SetAiTaskScheduleRequest { Schedule = "not a cron" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await projectWriterClient.PutAsJsonAsync(
            $"/api/backups/{world.BackupA1}/schedule", new SetBackupScheduleRequest { ScheduleCron = "0 0 2 * * *" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await projectWriterClient.PutAsJsonAsync(
            $"/api/ai/tasks/{world.AiOfServerA}/schedule", new SetAiTaskScheduleRequest { Schedule = "0 0 2 * * *" }, ct)).StatusCode);

        // Write on a server: only the AI task owned by that server shows and moves; no project backup.
        var onServer = Assert.Single((await ReadPlanAsync(serverWriterClient, world.OrganizationA)).ServerActions);
        Assert.Equal($"ai:{world.AiOfServerA}", onServer.Id);
        Assert.True(onServer.Movable);
        var serverTaskMoved = await serverWriterClient.PutAsJsonAsync(
            $"/api/ai/tasks/{world.AiOfServerA}/schedule", new SetAiTaskScheduleRequest { Schedule = "0 30 4 * * *" }, ct);
        Assert.True(serverTaskMoved.IsSuccessStatusCode, await serverTaskMoved.Content.ReadAsStringAsync(ct));
        Assert.Equal(4, Assert.Single((await ReadPlanAsync(serverWriterClient, world.OrganizationA)).ServerActions).Hour);

        // A site Admin sees every AI task and backup of the organization it reads, movable, and never another's.
        var all = (await ReadPlanAsync(admin, world.OrganizationA)).ServerActions;
        Assert.Equal(
            new[] { $"ai:{world.AiOfProjectA1}", $"ai:{world.AiOfServerA}", $"backup:{world.BackupA1}", $"backup:{world.BackupA2}" }.Order(),
            all.Select(action => action.Id).Order());
        Assert.All(all, action => Assert.True(action.Movable));
        Assert.Equal(
            new[] { $"ai:{world.AiOfProjectB}", $"backup:{world.BackupB}" }.Order(),
            (await ReadPlanAsync(admin, world.OrganizationB)).ServerActions.Select(action => action.Id).Order());
    }

    private static async Task<SchedulePlanDto> ReadPlanAsync(HttpClient client, int organizationId)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.GetAsync(Url(organizationId), ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return await response.Content.ReadFromJsonAsync<SchedulePlanDto>(IntegrationJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("The plan came back empty.");
    }

    private static async Task<World> SeedAsync(AetheusWebApplicationFactory factory)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationA = await SeedOrganizationAsync(db, "a");
        var organizationB = await SeedOrganizationAsync(db, "b");
        var projectA1 = new Project { Name = $"act-a1-{Guid.NewGuid():N}"[..24], OrganizationId = organizationA };
        var projectA2 = new Project { Name = $"act-a2-{Guid.NewGuid():N}"[..24], OrganizationId = organizationA };
        var projectB = new Project { Name = $"act-b-{Guid.NewGuid():N}"[..24], OrganizationId = organizationB };
        var serverA = new Server { Name = $"act-sa-{Guid.NewGuid():N}"[..24], Hostname = $"act-sa-{Guid.NewGuid():N}"[..24], Status = ServerStatus.Online, OrganizationId = organizationA };
        var serverB = new Server { Name = $"act-sb-{Guid.NewGuid():N}"[..24], Hostname = $"act-sb-{Guid.NewGuid():N}"[..24], Status = ServerStatus.Online, OrganizationId = organizationB };
        var profileA = new AiRunnerProfile { OrganizationId = organizationA, Name = $"act-pa-{Guid.NewGuid():N}"[..24] };
        var profileB = new AiRunnerProfile { OrganizationId = organizationB, Name = $"act-pb-{Guid.NewGuid():N}"[..24] };
        db.AddRange(projectA1, projectA2, projectB, serverA, serverB, profileA, profileB);
        await db.SaveChangesAsync(ct);

        var aiOfProjectA1 = new AiTaskDefinition { Name = "review", ProfileId = profileA.Id, ProjectId = projectA1.Id, Schedule = "0 30 1 * * *" };
        var aiOfServerA = new AiTaskDefinition { Name = "triage", ProfileId = profileA.Id, ServerId = serverA.Id, Schedule = "0 0 3 * * *" };
        var aiOfProjectB = new AiTaskDefinition { Name = "foreign-review", ProfileId = profileB.Id, ProjectId = projectB.Id, Schedule = "0 30 1 * * *" };
        var backupA1 = new BackupPolicy { Name = "orders", ProjectId = projectA1.Id, ServerId = serverA.Id, ScheduleCron = "15 1 * * *" };
        var backupA2 = new BackupPolicy { Name = "billing", ProjectId = projectA2.Id, ServerId = serverA.Id, ScheduleCron = "45 1 * * *" };
        var backupB = new BackupPolicy { Name = "foreign-orders", ProjectId = projectB.Id, ServerId = serverB.Id, ScheduleCron = "15 1 * * *" };
        db.AddRange(aiOfProjectA1, aiOfServerA, aiOfProjectB, backupA1, backupA2, backupB);
        await db.SaveChangesAsync(ct);
        return new World(
            organizationA, organizationB, projectA1.Id, projectA2.Id, projectB.Id, serverA.Id, serverB.Id,
            aiOfProjectA1.Id, aiOfServerA.Id, aiOfProjectB.Id, backupA1.Id, backupA2.Id, backupB.Id);
    }

    private static async Task<int> SeedOrganizationAsync(AppDbContext db, string label)
    {
        var organization = new Organization { Name = $"act-{label}-{Guid.NewGuid():N}", Slug = $"a{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return organization.Id;
    }
}
