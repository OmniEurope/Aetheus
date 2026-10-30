// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Tests;

public sealed class AdminEndpointAuthorizationMatrixTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task RoleMembershipAndOrganizationEndpoints_NonAdmin_Return403WithoutMutation()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.UserToken());
        var before = await CaptureStateAsync(factory);
        var requests = CreateSensitiveRequests();

        foreach (var request in requests)
        {
            using (request)
            using (var response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken))
            {
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"{request.Method} {request.RequestUri} returned {(int)response.StatusCode} "
                    + $"({response.StatusCode}) instead of 403.");
            }
        }

        var after = await CaptureStateAsync(factory);
        Assert.Equal(before, after);
    }

    private static List<HttpRequestMessage> CreateSensitiveRequests()
    {
        var requests = new List<HttpRequestMessage>
        {
            new(HttpMethod.Get, "/api/roles"),
            new(HttpMethod.Get, "/api/roles/assignable?organizationId=1"),
            WithJson(HttpMethod.Post, "/api/users", new { username = "forbidden-user", email = "forbidden-user@test.com", organizationRoles = new[] { new { organizationId = 1, role = RoleNames.Reader } } }),
            new(HttpMethod.Get, "/api/roles/1"),
            new(HttpMethod.Get, "/api/roles/1/permissions"),
            new(HttpMethod.Get, "/api/roles/1/users"),
            new(HttpMethod.Get, "/api/roles/1/available-users"),
            new(HttpMethod.Get, "/api/users/1/effective-permissions"),
            WithJson(HttpMethod.Post, "/api/roles", new { name = "forbidden-role" }),
            WithJson(HttpMethod.Put, "/api/roles/1", new { name = "forbidden-role" }),
            WithJson(HttpMethod.Put, "/api/roles/1/permissions", new { permissions = Array.Empty<object>() }),
            new(HttpMethod.Post, "/api/roles/1/clone"),
            WithJson(HttpMethod.Post, "/api/roles/1/users", new { userId = 1 }),
            new(HttpMethod.Delete, "/api/roles/1/users/1"),
            new(HttpMethod.Delete, "/api/roles/1"),

            new(HttpMethod.Get, "/api/organizations"),
            new(HttpMethod.Get, "/api/organizations/1"),
            new(HttpMethod.Get, "/api/users/1/organizations"),
            WithJson(HttpMethod.Post, "/api/organizations", new { name = "forbidden-org" }),
            WithJson(HttpMethod.Put, "/api/organizations/1", new { name = "forbidden-org" }),
            WithJson(HttpMethod.Post, "/api/organizations/1/members", new { userId = 1 }),
            WithJson(HttpMethod.Put, "/api/organizations/1/members/1", new { role = "Owner" }),
            WithJson(HttpMethod.Put, "/api/organizations/1/projects", new { projectIds = Array.Empty<int>() }),
            new(HttpMethod.Delete, "/api/organizations/1/members/1"),
            new(HttpMethod.Delete, "/api/organizations/1")
        };
        return requests;
    }

    private static HttpRequestMessage WithJson(HttpMethod method, string uri, object body) =>
        new(method, uri) { Content = JsonContent.Create(body) };

    private static async Task<ProtectedState> CaptureStateAsync(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roles = await db.Roles.OrderBy(item => item.Id)
            .Select(item => $"{item.Id}:{item.Name}:{item.Description}")
            .ToListAsync(TestContext.Current.CancellationToken);
        var permissions = await db.ResourcePermissions.OrderBy(item => item.Id)
            .Select(item => $"{item.Id}:{item.RoleId}:{item.ResourceType}:{item.ResourceId}:{item.Permission}")
            .ToListAsync(TestContext.Current.CancellationToken);
        var userRoles = await db.UserRoles.OrderBy(item => item.UserId).ThenBy(item => item.RoleId)
            .Select(item => $"{item.UserId}:{item.RoleId}")
            .ToListAsync(TestContext.Current.CancellationToken);
        var users = await db.Users.OrderBy(item => item.Id)
            .Select(item => $"{item.Id}:{item.Username}:{item.Email}:{item.IsActive}:{item.SecurityStamp}")
            .ToListAsync(TestContext.Current.CancellationToken);
        var organizations = await db.Organizations.OrderBy(item => item.Id)
            .Select(item => $"{item.Id}:{item.Name}:{item.Slug}:{item.Description}")
            .ToListAsync(TestContext.Current.CancellationToken);
        var members = await db.OrganizationMembers.OrderBy(item => item.Id)
            .Select(item => $"{item.Id}:{item.OrganizationId}:{item.UserId}:{item.Role}:{item.CreatedAt:O}")
            .ToListAsync(TestContext.Current.CancellationToken);
        var projects = await db.Projects.OrderBy(item => item.Id)
            .Select(item => $"{item.Id}:{item.OrganizationId}:{item.Name}")
            .ToListAsync(TestContext.Current.CancellationToken);
        var fingerprint = string.Join("||",
            string.Join("|", roles),
            string.Join("|", permissions),
            string.Join("|", userRoles),
            string.Join("|", users),
            string.Join("|", organizations),
            string.Join("|", members),
            string.Join("|", projects));
        return new ProtectedState(
            roles.Count,
            permissions.Count,
            userRoles.Count,
            users.Count,
            organizations.Count,
            members.Count,
            projects.Count,
            fingerprint);
    }

    private sealed record ProtectedState(
        int Roles,
        int ResourcePermissions,
        int UserRoles,
        int Users,
        int Organizations,
        int OrganizationMembers,
        int Projects,
        string Fingerprint);
}
