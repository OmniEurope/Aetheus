// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Verifies that SignalR hubs broadcast messages when server state changes.
/// <para>
/// The tests connect a real <see cref="HubConnection"/> to the hosted ServerHub and
/// subscribe to events, then trigger mutations through the HTTP API (or directly via
/// services). This exercises the full broadcast path (service → IHubContext → client)
/// against the real Kestrel pipeline and PostgreSQL - a path the InMemory unit suite
/// mocks away entirely (it uses <c>Mock&lt;IHubContext&gt;</c>).
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SignalRBroadcastIntegrationTests(PostgresFixture fixture)
{
    private static Task<string> LoginAsAdminAsync(HttpClient client)
        => IntegrationAuth.LoginAsAdminAsync(client, TestContext.Current.CancellationToken);

    /// <summary>
    /// Builds a SignalR connection to the test server's hub, using the admin JWT for auth.
    /// </summary>
    private static HubConnection BuildHubConnection(AetheusWebApplicationFactory factory, string hubPath, string token)
    {
        var server = factory.Server;
        return new HubConnectionBuilder()
            .WithUrl($"{server.BaseAddress}{hubPath}", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    [Fact]
    public async Task ServerHub_ReceivesUpdateAndRemovalBroadcasts()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var adminToken = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Register a server by seeding it directly (servers are created via registration token,
        // not a simple POST - so we seed one for the update test).
        int serverId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var defaultOrg = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstAsync(db.Organizations.Where(o => o.Slug == "aetheus"), cancellationToken: TestContext.Current.CancellationToken);
            var server = new Server
            {
                Name = $"hub-test-{Guid.NewGuid():N}",
                Hostname = $"hub-{Guid.NewGuid():N}.test",
                OrganizationId = defaultOrg.Id
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            serverId = server.Id;
        }

        // Connect to the ServerHub
        await using var hub = BuildHubConnection(factory, "hubs/servers", adminToken);

        var updateTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerDto>("ServerUpdated", dto =>
        {
            if (dto.Id == serverId)
                updateTcs.TrySetResult(true);
        });
        hub.On<int>("ServerRemoved", id =>
        {
            if (id == serverId)
                removalTcs.TrySetResult(id);
        });

        await hub.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await hub.InvokeAsync("JoinAllServers", cancellationToken: TestContext.Current.CancellationToken);

        // Update the server via HTTP to trigger the broadcast
        var updateResp = await client.PutAsJsonAsync($"/api/servers/{serverId}", new UpdateServerRequest
        {
            Tags = ["signalr-test"]
        }, cancellationToken: TestContext.Current.CancellationToken);
        updateResp.EnsureSuccessStatusCode();

        // Wait for the broadcast (with timeout to avoid hanging)
        await Task.WhenAny(updateTcs.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(updateTcs.Task.IsCompletedSuccessfully, "Did not receive ServerUpdated broadcast within timeout.");

        // The same live connection must also receive the deletion event.
        var delResp = await client.DeleteAsync($"/api/servers/{serverId}", cancellationToken: TestContext.Current.CancellationToken);
        delResp.EnsureSuccessStatusCode();

        await Task.WhenAny(removalTcs.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(removalTcs.Task.IsCompletedSuccessfully, "Did not receive ServerRemoved broadcast within timeout.");
        Assert.Equal(serverId, await removalTcs.Task);

        await hub.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ServerRegistered_ReachesNonAdminMemberAlreadyJoinedToOrganizationGroup()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        var adminToken = await LoginAsAdminAsync(client);
        IntegrationAuth.SetBearer(client, adminToken);

        int organizationId;
        var username = $"hub-member-{Guid.NewGuid():N}";
        const string password = "Hub-Member-Pwd-2026!";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            organizationId = await db.Organizations
                .Where(organization => organization.Slug == "aetheus")
                .Select(organization => organization.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            var contributorRoleId = await db.Roles
                .Where(role => role.Name == "Contributor")
                .Select(role => role.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            var member = new User
            {
                Username = username,
                Email = $"{username}@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                IsActive = true
            };
            db.Users.Add(member);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            // ADR-059: the Contributor role is held for the organization the member belongs to.
            db.UserOrganizationRoles.Add(new UserOrganizationRole
            {
                UserId = member.Id,
                OrganizationId = organizationId,
                RoleId = contributorRoleId
            });
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = organizationId,
                UserId = member.Id,
                Role = OrganizationRole.Member
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var memberToken = await IntegrationAuth.LoginByEmailAsync(
            client,
            $"{username}@example.com",
            password,
            TestContext.Current.CancellationToken);
        await using var hub = BuildHubConnection(factory, "hubs/servers", memberToken);
        var registered = new TaskCompletionSource<ServerDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerDto>("ServerRegistered", dto => registered.TrySetResult(dto));
        await hub.StartAsync(TestContext.Current.CancellationToken);
        await hub.InvokeAsync("JoinAllServers", cancellationToken: TestContext.Current.CancellationToken);

        IntegrationAuth.SetBearer(client, adminToken);
        var tokenResponse = await client.PostAsJsonAsync(
            "/api/auth/registration-tokens",
            new CreateRegistrationTokenRequest { OrganizationId = organizationId, ExpirationHours = 1 },
            TestContext.Current.CancellationToken);
        tokenResponse.EnsureSuccessStatusCode();
        var registrationToken = await tokenResponse.Content.ReadFromJsonAsync<RegistrationTokenDto>(
            IntegrationJsonOptions.Default,
            TestContext.Current.CancellationToken);
        Assert.NotNull(registrationToken);

        IntegrationAuth.SetBearer(client, null);
        var registerResponse = await client.PostAsJsonAsync(
            "/api/auth/register",
            new ServerRegistrationRequest
            {
                RegistrationToken = registrationToken.Token,
                Hostname = $"new-org-server-{Guid.NewGuid():N}",
                OsDescription = "Linux"
            },
            TestContext.Current.CancellationToken);
        registerResponse.EnsureSuccessStatusCode();

        var received = await registered.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.Equal(organizationId, received.OrganizationId);
    }
}
