// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The pipeline run's registry credential through the real HTTP pipeline and PostgreSQL. The server mints
/// it only when the pipeline owner is an active administrator; it then publishes the observability
/// packages (NuGet and npm) and reads them back, and nothing more. Another package and an unlist are
/// refused, as are the tokens of a finished run, of a pipeline whose owner is not an administrator or is
/// disabled, a tampered or an expired token, and a live token once its owner loses the Admin role.
/// </summary>
public sealed class PackageRegistryRunTokenIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string NuGet = "/api/packages/nuget/";
    private const string Npm = "/api/packages/npm/";
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "aetheus-run-token-registry-it", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAdministratorOwnedRunsToken_PublishesOnlyTheObservabilityPackages_AndEveryOtherTokenIsRefused()
    {
        await fixture.ResetAsync();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("PackageRegistry:BasePath", _storage));
        var seeded = await SeedAsync(host.Services);

        // Minting: only the administrator-owned pipeline gets a token; the others are refused with the reason.
        var token = (await MintAsync(host.Services, seeded.AdminRun, seeded.Project)).Token!;
        Assert.NotNull(token);
        Assert.Contains("Admin role", (await MintAsync(host.Services, seeded.DeveloperRun, seeded.Project)).Refusal, StringComparison.Ordinal);
        Assert.Contains("disabled", (await MintAsync(host.Services, seeded.DisabledOwnerRun, seeded.Project)).Refusal, StringComparison.Ordinal);
        Assert.Contains("no owner", (await MintAsync(host.Services, seeded.UnownedRun, seeded.Project)).Refusal, StringComparison.Ordinal);

        // NuGet push the way `dotnet nuget push` sends it (Basic password), npm publish with the bearer.
        using var client = host.CreateClient();
        var push = await SendAsync(client, HttpMethod.Put, NuGet + "v2/package", BasicAuth(token),
            PackageRegistryAnonymousDownloadIntegrationTests.NuGetPushContent("Aetheus.Telemetry", "1.0.0"));
        Assert.Equal(HttpStatusCode.Created, push.StatusCode);
        var npmPublish = await SendAsync(client, HttpMethod.Put, Npm + "@aetheus%2fweb-analytics", Bearer(token),
            NpmContent("@aetheus/web-analytics", "1.0.0"));
        Assert.Equal(HttpStatusCode.Created, npmPublish.StatusCode);

        // The harness reads its publication back with the same token.
        var readBack = await SendAsync(client, HttpMethod.Get,
            NuGet + "v3/flatcontainer/aetheus.telemetry/1.0.0/aetheus.telemetry.1.0.0.nupkg", BasicAuth(token));
        Assert.Equal(HttpStatusCode.OK, readBack.StatusCode);

        // Only the observability packages: another package is refused and nothing is stored.
        var other = await SendAsync(client, HttpMethod.Put, NuGet + "v2/package", BasicAuth(token),
            PackageRegistryAnonymousDownloadIntegrationTests.NuGetPushContent("Aetheus.Private", "1.0.0"));
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        var otherNpm = await SendAsync(client, HttpMethod.Put, Npm + "@aetheus%2fprivate", Bearer(token),
            NpmContent("@aetheus/private", "1.0.0"));
        Assert.Equal(HttpStatusCode.BadRequest, otherNpm.StatusCode);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(
                ["@aetheus/web-analytics", "aetheus.telemetry"],
                (await db.RegistryPackages.Select(package => package.NormalizedName).ToListAsync(Ct)).Order(StringComparer.Ordinal));
        }

        // No administration: unlisting stays administrator-only.
        var unlist = await SendAsync(client, HttpMethod.Delete, NuGet + "v2/package/aetheus.telemetry/1.0.0", BasicAuth(token));
        Assert.Equal(HttpStatusCode.Forbidden, unlist.StatusCode);

        // Refused tokens, each correctly signed by the server key unless stated: the run has finished, the
        // owner is not an administrator, the owner is disabled, the signature is altered, it expired.
        var tokens = new PackageRunTokens(host.Services.GetRequiredService<TokenHashKeys>(), TimeProvider.System);
        var expired = new PackageRunTokens(host.Services.GetRequiredService<TokenHashKeys>(),
                new FixedTimeProvider(DateTimeOffset.UtcNow - PackageRunTokens.Ttl - TimeSpan.FromMinutes(1)))
            .Mint(seeded.AdminRun, seeded.Project);
        var refused = new[]
        {
            ("finished run", tokens.Mint(seeded.FinishedRun, seeded.Project)),
            ("owner not admin", tokens.Mint(seeded.DeveloperRun, seeded.Project)),
            ("owner disabled", tokens.Mint(seeded.DisabledOwnerRun, seeded.Project)),
            ("tampered", token[..^1] + (token[^1] == '0' ? '1' : '0')),
            ("expired", expired)
        };
        foreach (var (name, candidate) in refused)
            await AssertPushUnauthorizedAsync(client, name, candidate);

        // Demoting the owner revokes the live token at once.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.UserRoles.Where(userRole => userRole.User.Username == seeded.AdminOwner).ExecuteDeleteAsync(Ct);
        }
        await AssertPushUnauthorizedAsync(client, "owner demoted", token);
    }

    private sealed record Seeded(
        int Project, string AdminOwner, int AdminRun, int FinishedRun, int DeveloperRun, int DisabledOwnerRun, int UnownedRun);

    private static async Task<Seeded> SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var adminRole = await db.Roles.SingleAsync(role => role.Name == RoleNames.Admin && role.OrganizationId == null, Ct);
        var admin = User($"pkg-admin-{marker}", active: true);
        var developer = User($"pkg-dev-{marker}", active: true);
        var disabled = User($"pkg-disabled-{marker}", active: false);
        db.Users.AddRange(admin, developer, disabled);
        await db.SaveChangesAsync(Ct);
        db.UserRoles.AddRange(
            new UserRole { UserId = admin.Id, RoleId = adminRole.Id },
            new UserRole { UserId = disabled.Id, RoleId = adminRole.Id });

        var organization = new Organization { Name = "Run token", Slug = $"run-token-{marker}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = "Aetheus", OrganizationId = organization.Id, DefaultBranch = "main" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        var adminPipeline = Pipeline(project.Id, "publish", admin.Username);
        var developerPipeline = Pipeline(project.Id, "publish-dev", developer.Username);
        var disabledPipeline = Pipeline(project.Id, "publish-disabled", disabled.Username);
        var unownedPipeline = Pipeline(project.Id, "publish-unowned", null);
        db.Pipelines.AddRange(adminPipeline, developerPipeline, disabledPipeline, unownedPipeline);
        await db.SaveChangesAsync(Ct);
        var adminRun = Run(adminPipeline.Id, PipelineStatus.Running);
        var finishedRun = Run(adminPipeline.Id, PipelineStatus.Success);
        var developerRun = Run(developerPipeline.Id, PipelineStatus.Running);
        var disabledRun = Run(disabledPipeline.Id, PipelineStatus.Running);
        var unownedRun = Run(unownedPipeline.Id, PipelineStatus.Running);
        db.PipelineRuns.AddRange(adminRun, finishedRun, developerRun, disabledRun, unownedRun);
        await db.SaveChangesAsync(Ct);
        return new Seeded(project.Id, admin.Username, adminRun.Id, finishedRun.Id, developerRun.Id, disabledRun.Id, unownedRun.Id);
    }

    private static User User(string username, bool active) => new()
    {
        Username = username,
        Email = $"{username}@example.test",
        PasswordHash = "not-a-real-hash",
        IsActive = active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static Pipeline Pipeline(int projectId, string name, string? owner) => new()
    {
        Name = name,
        ProjectId = projectId,
        CreatedByUsername = owner,
        YamlDefinition = "name: p\ntrigger: manual\nstages: []"
    };

    private static PipelineRun Run(int pipelineId, PipelineStatus status) =>
        new() { PipelineId = pipelineId, Status = status, StartedAt = DateTime.UtcNow };

    private static async Task<PackageRunTokenGrant> MintAsync(IServiceProvider services, int runId, int projectId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPackageRunTokenIssuer>().MintAsync(runId, projectId, Ct);
    }

    private static async Task AssertPushUnauthorizedAsync(HttpClient client, string name, string token)
    {
        var response = await SendAsync(client, HttpMethod.Put, NuGet + "v2/package", BasicAuth(token),
            PackageRegistryAnonymousDownloadIntegrationTests.NuGetPushContent("Aetheus.WebAnalytics", "1.0.0"));
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name} token: {response.StatusCode}");
    }

    private static AuthenticationHeaderValue BasicAuth(string token) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("aetheus:" + token)));

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private static StringContent NpmContent(string name, string version) => new(
        PackageRegistryAnonymousDownloadIntegrationTests.NpmPublishDocument(name, version), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, AuthenticationHeaderValue authorization, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = authorization;
        return await client.SendAsync(request, Ct);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_storage))
            Directory.Delete(_storage, recursive: true);
    }
}
