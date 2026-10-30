// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-46, R-47, R-45 (ADR-059): strict organization isolation on the real host and PostgreSQL. A user
/// holds the built-in Contributor role for organization B only: its wildcard Write permission reaches
/// B's servers, projects, pipelines, runs and monitored apps (positive control) and nothing of A, in
/// lists, by-id reads, writes, notifications and SignalR. The site Admin still sees both organizations,
/// and a membership given from either side (organization or user) carries the mapped role.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationIsolationIntegrationTests(PostgresFixture fixture)
{
    public static TheoryData<string> Families => ["projects", "servers", "pipelines", "pipeline-runs", "monitored-apps"];

    [Theory]
    [MemberData(nameof(Families))]
    public async Task ContributorOfOrganizationB_ListsAndReadsOnlyOrganizationB(string family)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await CreateUserHoldingAsync(factory, "iso-b", "Contributor", b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var (listUrl, idOf, byIdUrl) = family switch
        {
            "projects" => ("/api/projects?pageSize=200", (Func<Tenant, int>)(t => t.ProjectId), "/api/projects/{0}"),
            "servers" => ("/api/servers?pageSize=200", t => t.ServerId, "/api/servers/{0}"),
            "pipelines" => ("/api/pipelines?pageSize=200", t => t.PipelineId, "/api/pipelines/{0}"),
            "pipeline-runs" => ("/api/pipelines/runs/recent", t => t.RunId, "/api/pipelines/runs/{0}"),
            "monitored-apps" => ("/api/AppMonitoring/summary", t => t.AppId, "/api/AppMonitoring/apps/{0}"),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
        };

        var ids = await ReadIdsAsync(client, listUrl, ct);
        Assert.Contains(idOf(b), ids);
        Assert.DoesNotContain(idOf(a), ids);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(byIdUrl, idOf(b)), ct)).StatusCode);
        var foreign = await client.GetAsync(Url(byIdUrl, idOf(a)), ct);
        Assert.Contains(foreign.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.DoesNotContain(a.Marker, await foreign.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // The site Admin is site-wide: it lists both organizations' rows.
        var adminIds = await ReadIdsAsync(admin, listUrl, ct);
        Assert.Contains(idOf(a), adminIds);
        Assert.Contains(idOf(b), adminIds);
    }

    [Fact]
    public async Task ContributorOfOrganizationB_CannotWriteIntoOrganizationA()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await CreateUserHoldingAsync(factory, "iso-write", "Contributor", b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        var refused = new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound };
        Assert.Contains((await client.PutAsJsonAsync($"/api/servers/{a.ServerId}",
            new UpdateServerRequest { Tags = ["iso"] }, ct)).StatusCode, refused);
        Assert.Contains((await client.PostAsJsonAsync("/api/pipelines", new CreatePipelineRequest
        {
            Name = $"iso-{Guid.NewGuid():N}",
            YamlDefinition = "name: t\nstages: []",
            ProjectId = a.ProjectId
        }, ct)).StatusCode, refused);
        Assert.Contains((await client.PostAsJsonAsync($"/api/AppMonitoring/projects/{a.ProjectId}/apps",
            new CreateMonitoredAppRequest { ProjectId = a.ProjectId, Name = $"forged-{Guid.NewGuid():N}" }, ct)).StatusCode, refused);
        Assert.Contains((await client.DeleteAsync($"/api/pipelines/{a.PipelineId}", ct)).StatusCode, refused);

        // Positive control: the same Write role writes into its own organization's server.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/servers/{b.ServerId}",
            new UpdateServerRequest { Tags = ["iso"] }, ct)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Pipelines.AnyAsync(pipeline => pipeline.Id == a.PipelineId, ct));
        Assert.False(await db.MonitoredApps.AnyAsync(app => app.ProjectId == a.ProjectId && app.Name.StartsWith("forged-"), ct));
    }

    [Fact]
    public async Task ProjectNotification_ReachesOnlyTheSubscribersOfTheProjectOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userA = await CreateUserHoldingAsync(factory, "notif-a", "Reader", a.OrganizationId);
        var userB = await CreateUserHoldingAsync(factory, "notif-b", "Contributor", b.OrganizationId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Both subscribe to A's project (a subscription made while the user still had access, or forged).
        db.ProjectSubscriptions.AddRange(
            new ProjectSubscription { UserId = userA.Id, ProjectId = a.ProjectId, CreatedAt = DateTime.UtcNow },
            new ProjectSubscription { UserId = userB.Id, ProjectId = a.ProjectId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);

        var notifications = scope.ServiceProvider.GetRequiredService<IUserNotificationService>();
        var written = await notifications.RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed, $"{{\"ProjectId\":{a.ProjectId}}}", ct);

        var recipients = await db.NotificationDeliveries.AsNoTracking()
            .Where(delivery => delivery.RecipientUserId == userA.Id || delivery.RecipientUserId == userB.Id)
            .Select(delivery => delivery.RecipientUserId)
            .ToListAsync(ct);
        Assert.Equal(1, written);
        Assert.Equal([userA.Id], recipients);
    }

    [Fact]
    public async Task ServerHub_ContributorOfOrganizationB_NeverReceivesOrganizationAServerEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await CreateUserHoldingAsync(factory, "hub-b", "Contributor", b.OrganizationId);
        using var client = factory.CreateClient();
        var token = await IntegrationUsers.LoginTokenAsync(factory, client, userB.Username);

        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{factory.Server.BaseAddress}hubs/servers", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var removed = new List<int>();
        var ownRemoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<int>("ServerRemoved", id =>
        {
            lock (removed) removed.Add(id);
            if (id == b.ServerId) ownRemoved.TrySetResult();
        });
        await hub.StartAsync(ct);
        await hub.InvokeAsync("JoinAllServers", ct);

        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        (await admin.DeleteAsync($"/api/servers/{a.ServerId}", ct)).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/servers/{b.ServerId}", ct)).EnsureSuccessStatusCode();

        // A's removal was sent first on the same connection: once B's arrives, A's would have too.
        await ownRemoved.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        // A connection in both server-{id} and its org group may get B's removal twice; never A's.
        lock (removed) Assert.Equal([b.ServerId], removed.Distinct());
    }

    [Fact]
    public async Task Membership_GivenFromEitherSide_CarriesTheMappedRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var owner = await CreateUserHoldingAsync(factory, "map-owner", "Reader", b.OrganizationId);
        var member = await CreateUserHoldingAsync(factory, "map-member", "Reader", b.OrganizationId);
        var maintainer = await CreateUserHoldingAsync(factory, "map-maint", "Reader", b.OrganizationId);

        // From the organization side (R-45): the membership level maps to a role for that organization.
        foreach (var (userId, level) in new[]
                 {
                     (owner.Id, OrganizationRole.Owner), (member.Id, OrganizationRole.Member),
                     (maintainer.Id, OrganizationRole.Maintainer)
                 })
        {
            (await admin.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members",
                new AddOrganizationMemberRequest { UserId = userId, Role = level }, ct)).EnsureSuccessStatusCode();
        }

        Assert.Equal("Organization Admin", await RoleForAsync(admin, owner.Id, a.OrganizationId, ct));
        Assert.Equal("Reader", await RoleForAsync(admin, member.Id, a.OrganizationId, ct));
        Assert.Equal("Contributor", await RoleForAsync(admin, maintainer.Id, a.OrganizationId, ct));

        // The mapped Reader role really reads A now, and the new member still cannot write there.
        using var memberClient = await IntegrationUsers.LoginClientAsync(factory, member.Username);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync($"/api/projects/{a.ProjectId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PutAsJsonAsync($"/api/servers/{a.ServerId}",
            new UpdateServerRequest { Tags = ["iso"] }, ct)).StatusCode);

        // From the user side (R-47): giving a role for an organization makes the user a member of it.
        var fromUserSide = await CreateUserHoldingAsync(factory, "map-user", "Reader", b.OrganizationId);
        (await admin.PutAsJsonAsync($"/api/users/{fromUserSide.Id}", new UpdateUserRequest
        {
            Username = fromUserSide.Username,
            Email = $"{fromUserSide.Username}@example.com",
            IsActive = true,
            Roles = [],
            OrganizationRoles =
            [
                new OrganizationRoleAssignmentDto { OrganizationId = a.OrganizationId, Role = "Reader" },
                new OrganizationRoleAssignmentDto { OrganizationId = b.OrganizationId, Role = "Reader" }
            ]
        }, ct)).EnsureSuccessStatusCode();
        var detail = await admin.GetFromJsonAsync<OrganizationDetailDto>(
            $"/api/organizations/{a.OrganizationId}", IntegrationJsonOptions.Default, ct);
        Assert.Contains(detail!.Members, item => item.UserId == fromUserSide.Id && item.Role == OrganizationRole.Member);
        Assert.Equal("Reader", await RoleForAsync(admin, fromUserSide.Id, a.OrganizationId, ct));
    }

    private sealed record Tenant(
        int OrganizationId, int ProjectId, int ServerId, int PipelineId, int RunId, int AppId, string Marker);

    private static string Url(string template, int id) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, template, id);

    private static async Task<List<int>> ReadIdsAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (!url.EndsWith("/summary", StringComparison.Ordinal))
            return await IntegrationUsers.ReadListIdsAsync(response);
        var summary = await response.Content.ReadFromJsonAsync<AppMonitoringSummaryDto>(IntegrationJsonOptions.Default, ct);
        return [.. summary!.Applications.Select(app => app.Id)];
    }

    private static async Task<string?> RoleForAsync(HttpClient admin, int userId, int organizationId, CancellationToken ct)
    {
        var user = await admin.GetFromJsonAsync<UserDto>($"/api/users/{userId}", IntegrationJsonOptions.Default, ct);
        return user!.OrganizationRoles.SingleOrDefault(item => item.OrganizationId == organizationId)?.Role;
    }

    /// <summary>A user holding the built-in <paramref name="roleName"/> for one organization, as a member of it.</summary>
    private static async Task<(int Id, string Username)> CreateUserHoldingAsync(
        AetheusWebApplicationFactory factory, string prefix, string roleName, int organizationId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleId = await db.Roles.Where(role => role.Name == roleName).Select(role => role.Id).SingleAsync(ct);
        var username = $"{prefix}-{Guid.NewGuid():N}";
        var user = new User
        {
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(IntegrationUsers.Password),
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = organizationId, RoleId = roleId });
        db.OrganizationMembers.Add(new OrganizationMember { UserId = user.Id, OrganizationId = organizationId, Role = OrganizationRole.Member });
        await db.SaveChangesAsync(ct);
        return (user.Id, user.Username);
    }

    /// <summary>One organization with a project, a server, a pipeline, a finished run and a monitored app.</summary>
    private static async Task<Tenant> SeedTenantAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var marker = $"iso-{label}-{Guid.NewGuid():N}";

        var organization = new Organization { Name = marker, Slug = $"i{label}{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"{marker}-project", OrganizationId = organization.Id };
        var server = new Server
        {
            Name = $"{marker}-server",
            Hostname = $"{marker}.test",
            OrganizationId = organization.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline { Name = $"{marker}-pipeline", ProjectId = project.Id, YamlDefinition = "name: t\nstages: []" };
        var app = new MonitoredApp { ProjectId = project.Id, Name = $"{marker}-app" };
        db.Pipelines.Add(pipeline);
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(ct);
        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return new Tenant(organization.Id, project.Id, server.Id, pipeline.Id, run.Id, app.Id, marker);
    }
}
