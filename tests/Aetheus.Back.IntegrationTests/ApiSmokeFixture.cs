// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Boots the real <see cref="Program"/> pipeline against PostgreSQL EXACTLY ONCE for the whole
/// API-surface smoke sweep, then logs in as the seeded bootstrap admin and caches the JWT. The two
/// sweep suites (<see cref="ApiAuthorizationSmokeTests"/> and <see cref="ApiReadSmokeTests"/>) each fire
/// dozens of HTTP requests; rebuilding the host per case (the per-test pattern used elsewhere) would
/// re-run MigrateAsync + SeedAsync every time and make the sweep prohibitively slow. Sharing one booted
/// host via a collection fixture keeps the heavy boot off the hot path while still exercising the genuine
/// controller → service → repository → Postgres stack for every API.
/// <para>
/// DRPS: this fixture uses the dedicated <c>aetheus_smoke</c> database on the SHARED assembly
/// container (<see cref="SharedPostgresContainer"/>), separate from the relational suites'
/// <c>aetheus_it</c> database - so its admin password / seed state can never be perturbed by the
/// relational suites that drop and reseed their own schema, yet only ONE Postgres boots for the whole
/// assembly (no second concurrent container).
/// </para>
/// <para>
/// CONTRACT - additive tests only. Every suite in <see cref="ApiSmokeCollection"/> shares this
/// one booted host, its seeded admin/server/project, and a 5-minute RBAC permission cache.
/// xUnit serialises the collection, so the shared state is safe ONLY as long as tests are
/// additive: create GUID-suffixed rows and delete what they create. Do NOT assert on a global
/// row count, and do NOT rely on a just-deleted resource id staying absent from the RBAC cache -
/// either would make a test order-dependent and flaky.
/// </para>
/// </summary>
public sealed class ApiSmokeFixture(SharedPostgresContainer shared) : IAsyncLifetime
{
    private readonly ConcurrentBag<int> _readerUserIds = [];

    public AetheusWebApplicationFactory Factory { get; private set; } = null!;

    public string AdminToken { get; private set; } = string.Empty;

    /// <summary>Id of a server seeded directly in the DB, for server-scoped read endpoints.</summary>
    public int ServerId { get; private set; }

    /// <summary>Id of a project seeded directly in the DB, for project-scoped read endpoints.</summary>
    public int ProjectId { get; private set; }

    /// <summary>Id of the pipeline that owns <see cref="PipelineRunId"/>, for pipeline-and-run-scoped reads.</summary>
    public int PipelineId { get; private set; }

    /// <summary>Id of a pipeline run seeded directly in the DB, for run-scoped read endpoints.</summary>
    public int PipelineRunId { get; private set; }

    public async ValueTask InitializeAsync()
    {
        // DRPS: use the shared assembly container's dedicated smoke database (no second container boot).
        Factory = new AetheusWebApplicationFactory(shared.SmokeConnectionString);

        using (var client = Factory.CreateClient())
        {
            AdminToken = await IntegrationAuth.LoginAsAdminAsync(client, TestContext.Current.CancellationToken);
        }

        // Seed a server AND a project so server-scoped (modules, apps, certbot, …) and
        // project-scoped (artifacts, gitgraph, work-items) read endpoints have a real,
        // authorized target. Both are created via registration token / API in production but
        // are seeded directly here, like SignalRBroadcastIntegrationTests.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var defaultOrg = await db.Organizations.FirstAsync(
            o => o.Slug == "aetheus",
            TestContext.Current.CancellationToken);
        var server = new Server
        {
            Name = $"smoke-{Guid.NewGuid():N}",
            Hostname = $"smoke-{Guid.NewGuid():N}.test",
            OrganizationId = defaultOrg.Id
        };
        db.Servers.Add(server);
        var project = new Project
        {
            Name = $"smoke-{Guid.NewGuid():N}",
            Description = "smoke read-path target",
            OrganizationId = defaultOrg.Id
        };
        db.Projects.Add(project);
        var pipeline = new Pipeline
        {
            Name = $"smoke-{Guid.NewGuid():N}",
            Description = "smoke run-path target",
            YamlDefinition = "name: smoke",
            Project = project
        };
        var run = new PipelineRun
        {
            Pipeline = pipeline,
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ServerId = server.Id;
        ProjectId = project.Id;
        PipelineId = pipeline.Id;
        PipelineRunId = run.Id;
    }

    /// <summary>
    /// Creates a brand-new <c>Reader</c>-role user over HTTP (as admin) and returns a client
    /// authenticated as that user. The seeded <c>Reader</c> role grants a GLOBAL <c>Read</c> on
    /// every resource type (so reads succeed) but no <c>Write</c>/<c>Admin</c> - making it the
    /// right subject for negative RBAC assertions: create/update/delete must be refused (403).
    /// </summary>
    public async Task<HttpClient> CreateReaderClientAsync()
    {
        var username = $"smoke-reader-{Guid.NewGuid():N}";
        const string password = "Smoke-Reader-Pwd-2026!";

        using (var admin = CreateAdminClient())
        {
            var createResp = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
            {
                Username = username,
                Email = $"{username}@example.com",
                OrganizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(admin, "Reader")
            }, cancellationToken: TestContext.Current.CancellationToken);
            createResp.EnsureSuccessStatusCode();
            var created = await createResp.Content.ReadFromJsonAsync<UserDto>(
                IntegrationJsonOptions.Default,
                cancellationToken: TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Reader creation returned an empty response body.");
            _readerUserIds.Add(created.Id);
            // D1: a flagged account only reaches the password change; this one tests RBAC.
            await IntegrationUsers.SetPasswordAsync(Factory.Services, created.Id, password);
        }

        var client = Factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginByEmailAsync(
            client,
            $"{username}@example.com",
            password,
            TestContext.Current.CancellationToken));
        return client;
    }

    /// <summary>A fresh client with NO auth header - for the anonymous-rejection sweep.</summary>
    public HttpClient CreateAnonymousClient() => Factory.CreateClient();

    /// <summary>A fresh client pre-authenticated as the bootstrap admin.</summary>
    public HttpClient CreateAdminClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var admin = CreateAdminClient();
            foreach (var userId in _readerUserIds.Distinct())
            {
                using var response = await admin.DeleteAsync($"/api/users/{userId}", cleanupTimeout.Token);
                if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound))
                {
                    throw new InvalidOperationException(
                        $"Reader cleanup failed for user {userId}: HTTP {(int)response.StatusCode}.");
                }
            }
        }
        finally
        {
            // The shared container is owned by the assembly fixture; only dispose the per-suite factory.
            await Factory.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiSmokeCollection : ICollectionFixture<ApiSmokeFixture>
{
    public const string Name = "API smoke collection";
}
