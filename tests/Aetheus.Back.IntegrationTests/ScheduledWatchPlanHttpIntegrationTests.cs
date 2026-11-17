// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-8, the CVE watch and the restore-check cadence of the build planner, through the real HTTP pipeline, JWT and RBAC,
/// on real PostgreSQL: the CVE watch is global data whose sources page is for site administrators, so a member of the
/// organization, whatever its rights, never sees it; a site Admin sees it, and moves it (its write endpoint is administrator only), as its one daily pass at the configured local
/// time (04:00 Europe/Paris), recorded pass or not. The restore-check cadence follows the read right on the project of its
/// policy and never crosses the organization. Nothing uses the network: the watch is on but its pass is at night and nothing was
/// ever recorded, so no cycle runs in the host.
/// </summary>
public sealed class ScheduledWatchPlanHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private sealed record World(
        int OrganizationA, int OrganizationB, int ProjectA, int ProjectHidden, int ProjectB, int PolicyA, int PolicyHidden, int PolicyB);

    private static WebApplicationFactory<Program> Host(AetheusWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("VulnerabilityWatch:Enabled", "true");
            builder.UseSetting("VulnerabilityWatch:PassTime", "04:00");
            builder.UseSetting("VulnerabilityWatch:PassTimeZone", "Europe/Paris");
            builder.UseSetting("VulnerabilityWatch:Ecosystems:0", "NuGet");
        });

    private static string Url(int organizationId)
    {
        var from = DateTime.UtcNow.Date;
        return $"/api/pipelines/schedule-plan?organizationId={organizationId}&from={from:yyyy-MM-ddTHH:mm:ssZ}&to={from.AddDays(2):yyyy-MM-ddTHH:mm:ssZ}";
    }

    private static async Task<SchedulePlanDto> ReadPlanAsync(HttpClient client, int organizationId)
    {
        var response = await client.GetAsync(Url(organizationId), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<SchedulePlanDto>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The plan came back empty.");
    }

    private static async Task<World> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationA = new Organization { Name = $"wat-a-{Guid.NewGuid():N}", Slug = $"a{Guid.NewGuid():N}"[..20] };
        var organizationB = new Organization { Name = $"wat-b-{Guid.NewGuid():N}", Slug = $"b{Guid.NewGuid():N}"[..20] };
        db.AddRange(organizationA, organizationB);
        await db.SaveChangesAsync(Ct);
        var projectA = new Project { Name = $"wat-pa-{Guid.NewGuid():N}"[..24], OrganizationId = organizationA.Id };
        var projectHidden = new Project { Name = $"wat-ph-{Guid.NewGuid():N}"[..24], OrganizationId = organizationA.Id };
        var projectB = new Project { Name = $"wat-pb-{Guid.NewGuid():N}"[..24], OrganizationId = organizationB.Id };
        var serverA = new Server { Name = $"wat-sa-{Guid.NewGuid():N}"[..24], Hostname = $"wat-sa-{Guid.NewGuid():N}"[..24], Status = ServerStatus.Online, OrganizationId = organizationA.Id };
        var serverB = new Server { Name = $"wat-sb-{Guid.NewGuid():N}"[..24], Hostname = $"wat-sb-{Guid.NewGuid():N}"[..24], Status = ServerStatus.Online, OrganizationId = organizationB.Id };
        db.AddRange(projectA, projectHidden, projectB, serverA, serverB);
        await db.SaveChangesAsync(Ct);
        var policyA = new BackupPolicy { Name = "orders", ProjectId = projectA.Id, ServerId = serverA.Id, ScheduleCron = "15 1 * * *", RestoreCheckCron = "0 5 * * 3" };
        var policyHidden = new BackupPolicy { Name = "billing", ProjectId = projectHidden.Id, ServerId = serverA.Id, ScheduleCron = "45 1 * * *", RestoreCheckCron = "0 6 * * 3" };
        var policyB = new BackupPolicy { Name = "foreign-orders", ProjectId = projectB.Id, ServerId = serverB.Id, ScheduleCron = "15 1 * * *", RestoreCheckCron = "0 5 * * 3" };
        // A policy with no cadence lists no restore check.
        var noCadence = new BackupPolicy { Name = "no-check", ProjectId = projectA.Id, ServerId = serverA.Id, ScheduleCron = "30 1 * * *" };
        db.AddRange(policyA, policyHidden, policyB, noCadence);
        await db.SaveChangesAsync(Ct);
        return new World(organizationA.Id, organizationB.Id, projectA.Id, projectHidden.Id, projectB.Id, policyA.Id, policyHidden.Id, policyB.Id);
    }

    [Fact]
    public async Task TheCveWatch_IsSeenAndMovedByASiteAdminOnly_AndIsOneDailyPassAt0400ParisUnlessMoved()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        var world = await SeedAsync(host.Services);
        // A member who reads pipelines only, a member who reads the project, and a member with the strongest right on
        // every kind of resource: none of them is a site administrator.
        var pipelinesOnly = await IntegrationUsers.CreateAsync(host.Services, "watch-none",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], world.OrganizationA);
        var projectReader = await IntegrationUsers.CreateAsync(host.Services, "watch-reader",
            [new IntegrationUsers.Grant(ResourceType.Project, world.ProjectA, Permission.Read)], world.OrganizationA);
        var everything = Enum.GetValues<ResourceType>().Select(type => new IntegrationUsers.Grant(type, null, Permission.Admin)).ToList();
        var powerful = await IntegrationUsers.CreateAsync(host.Services, "watch-powerful", everything, world.OrganizationA);
        using var noneClient = await IntegrationUsers.LoginClientAsync(host, pipelinesOnly.Username);
        using var readerClient = await IntegrationUsers.LoginClientAsync(host, projectReader.Username);
        using var powerfulClient = await IntegrationUsers.LoginClientAsync(host, powerful.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        // Nobody but the site Admin gets the CVE watch, whatever the rights on the resources.
        Assert.Empty((await ReadPlanAsync(noneClient, world.OrganizationA)).ServerActions);
        Assert.DoesNotContain(ServerActionKind.CveWatch, (await ReadPlanAsync(readerClient, world.OrganizationA)).ServerActions.Select(action => action.Kind));
        Assert.DoesNotContain(ServerActionKind.CveWatch, (await ReadPlanAsync(powerfulClient, world.OrganizationA)).ServerActions.Select(action => action.Kind));

        // The site Admin gets it once, movable, as the one daily pass of the host configuration (04:00 Paris unless moved), whatever
        // was recorded of the last pass.
        var adminActions = (await ReadPlanAsync(admin, world.OrganizationA)).ServerActions;
        var watch = Assert.Single(adminActions, action => action.Kind == ServerActionKind.CveWatch);
        Assert.Equal("cve:0", watch.Id);
        Assert.True(watch.Movable);
        Assert.Equal("04:00", watch.DailyTime);
        Assert.Equal("Europe/Paris", watch.DailyTimeZoneId);
        var next = Assert.NotNull(watch.NextRunUtc);
        Assert.True(next > DateTime.UtcNow.AddSeconds(-5) && next <= DateTime.UtcNow.AddHours(24).AddMinutes(1), $"next pass {next:O}");
        Assert.Equal(new TimeOnly(4, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(next, Paris)));
        Assert.NotEmpty(watch.Occurrences);
        Assert.All(watch.Occurrences, occurrence => Assert.Equal(new TimeOnly(4, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(occurrence.StartUtc, Paris))));
        Assert.All(watch.Occurrences, occurrence => Assert.Equal(0, occurrence.OverlapMinutes));
        // It belongs to the site: the same block for the other organization, whose own backups it does not carry.
        Assert.Single((await ReadPlanAsync(admin, world.OrganizationB)).ServerActions, action => action.Kind == ServerActionKind.CveWatch);
    }

    [Fact]
    public async Task TheCveWatch_WithNoPassRecorded_IsStillListedForTheAdminWithItsDailyTime()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        var world = await SeedAsync(host.Services);
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        var watch = Assert.Single((await ReadPlanAsync(admin, world.OrganizationA)).ServerActions, action => action.Kind == ServerActionKind.CveWatch);

        Assert.NotNull(watch.NextRunUtc);
        Assert.NotEmpty(watch.Occurrences);
    }

    [Fact]
    public async Task TheCveWatch_IsNotListedAtAll_WhenTheWatchIsDisabled_EvenForTheAdmin()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var world = await SeedAsync(factory.Services);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        Assert.DoesNotContain(ServerActionKind.CveWatch, (await ReadPlanAsync(admin, world.OrganizationA)).ServerActions.Select(action => action.Kind));
    }

    [Fact]
    public async Task TheRestoreCheck_FollowsTheReadRightOnItsProject_IsReadOnly_AndNeverCrossesTheOrganization()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        var world = await SeedAsync(host.Services);
        var none = await IntegrationUsers.CreateAsync(host.Services, "check-none",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], world.OrganizationA);
        // Read on project A, and the strongest right on the other organization's project: it reaches nothing here.
        var reader = await IntegrationUsers.CreateAsync(host.Services, "check-reader",
            [new IntegrationUsers.Grant(ResourceType.Project, world.ProjectA, Permission.Read),
             new IntegrationUsers.Grant(ResourceType.Project, world.ProjectB, Permission.Admin)], world.OrganizationA);
        var writer = await IntegrationUsers.CreateAsync(host.Services, "check-writer",
            [new IntegrationUsers.Grant(ResourceType.Project, world.ProjectA, Permission.Write)], world.OrganizationA);
        using var noneClient = await IntegrationUsers.LoginClientAsync(host, none.Username);
        using var readerClient = await IntegrationUsers.LoginClientAsync(host, reader.Username);
        using var writerClient = await IntegrationUsers.LoginClientAsync(host, writer.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        Assert.Empty((await ReadPlanAsync(noneClient, world.OrganizationA)).ServerActions);

        var read = (await ReadPlanAsync(readerClient, world.OrganizationA)).ServerActions;
        var check = Assert.Single(read, action => action.Kind == ServerActionKind.RestoreCheck);
        Assert.Equal($"restore:{world.PolicyA}", check.Id);
        Assert.Equal("0 5 * * 3", check.CronExpression);
        Assert.Equal(ScheduleFrequency.Weekly, check.Frequency);
        Assert.Equal("orders", check.SourceName);
        Assert.Equal(world.ProjectA, check.ProjectId);
        // The policy with no cadence has no restore check, the policy of a project the caller cannot read has none either.
        Assert.Equal(2, read.Count(action => action.Kind == ServerActionKind.AppBackup));
        Assert.DoesNotContain(read, action => action.Id == $"restore:{world.PolicyHidden}" || action.Id == $"restore:{world.PolicyB}");

        // Even with the Write right on the project, where the backup moves, the restore-check cadence is read only.
        var written = (await ReadPlanAsync(writerClient, world.OrganizationA)).ServerActions;
        Assert.All(written.Where(action => action.Kind == ServerActionKind.AppBackup), action => Assert.True(action.Movable));
        Assert.False(Assert.Single(written, action => action.Kind == ServerActionKind.RestoreCheck).Movable);

        // The site Admin sees both of the organization's cadences, and the other organization's only on its own plan.
        var adminA = (await ReadPlanAsync(admin, world.OrganizationA)).ServerActions
            .Where(action => action.Kind == ServerActionKind.RestoreCheck).Select(action => action.Id).Order().ToList();
        Assert.Equal(new[] { $"restore:{world.PolicyA}", $"restore:{world.PolicyHidden}" }.Order(), adminA);
        Assert.Equal(
            [$"restore:{world.PolicyB}"],
            (await ReadPlanAsync(admin, world.OrganizationB)).ServerActions.Where(action => action.Kind == ServerActionKind.RestoreCheck).Select(action => action.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync(Url(world.OrganizationB), Ct)).StatusCode);
    }
}
