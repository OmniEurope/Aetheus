// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-002 lot 10, gap 2: a user of organization B never reaches organization A's data through the
/// real list and by-id endpoints. User B is a member of B and, for B only (ADR-059), holds wildcard
/// Read on projects and servers and Read on B's own vault and pipeline, so every list must show B's row (positive control: the filter is not simply "nothing") and
/// never A's, and every by-id read of A's row is refused.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationTenantIsolationHttpIntegrationTests(PostgresFixture fixture)
{
    public static TheoryData<string> Families => ["projects", "servers", "vaults", "pipeline-runs"];

    [Theory]
    [MemberData(nameof(Families))]
    public async Task UserOfOrganizationB_ListsOnlyItsOwnRows_AndCannotFetchOrganizationARows(string family)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await IntegrationUsers.CreateAsync(factory.Services, "tenant-b",
            [
                new IntegrationUsers.Grant(ResourceType.Vault, b.VaultId, Permission.Read),
                new IntegrationUsers.Grant(ResourceType.Pipeline, b.PipelineId, Permission.Read),
                // ADR-059: wildcard grants, bounded by the organization the role is held for (B).
                new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read),
                new IntegrationUsers.Grant(ResourceType.Server, null, Permission.Read)
            ],
            b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        var (listUrl, idOf, byIdUrl) = family switch
        {
            "projects" => ("/api/projects?pageSize=200", (Func<Tenant, int>)(t => t.ProjectId), "/api/projects/{0}"),
            "servers" => ("/api/servers?pageSize=200", t => t.ServerId, "/api/servers/{0}"),
            "vaults" => ("/api/vaults?pageSize=200", t => t.VaultId, "/api/vaults/{0}"),
            "pipeline-runs" => ("/api/pipelines/runs/recent", t => t.RunId, "/api/pipelines/runs/{0}"),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
        };

        var list = await client.GetAsync(listUrl, ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var ids = await IntegrationUsers.ReadListIdsAsync(list);
        Assert.Contains(idOf(b), ids);
        Assert.DoesNotContain(idOf(a), ids);

        var own = await client.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, byIdUrl, idOf(b)), ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        var foreign = await client.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, byIdUrl, idOf(a)), ct);
        Assert.Contains(foreign.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        var body = await foreign.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(a.Marker, body, StringComparison.Ordinal);

        if (family == "pipeline-runs")
        {
            // The per-pipeline run list of A's pipeline is refused as a whole.
            var foreignRuns = await client.GetAsync($"/api/pipelines/{a.PipelineId}/runs", ct);
            Assert.Contains(foreignRuns.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }
    }

    [Fact]
    public async Task UserOfOrganizationB_CannotListOrDownloadOrganizationAArtifacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await IntegrationUsers.CreateAsync(factory.Services, "tenant-b-artifacts",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        // Positive control: a Project Read role held for B (ADR-059) downloads B's artifact, bytes intact.
        var ownDownload = await client.GetAsync($"/api/artifacts/{b.ArtifactId}/download", ct);
        Assert.Equal(HttpStatusCode.OK, ownDownload.StatusCode);
        Assert.Equal(b.ArtifactBytes, await ownDownload.Content.ReadAsByteArrayAsync(ct));

        var foreignList = await client.GetAsync($"/api/artifacts/project/{a.ProjectId}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignList.StatusCode);

        var foreignMetadata = await client.GetAsync($"/api/artifacts/{a.ArtifactId}", ct);
        Assert.Contains(foreignMetadata.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        var foreignDownload = await client.GetAsync($"/api/artifacts/{a.ArtifactId}/download", ct);
        Assert.Contains(foreignDownload.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        var leaked = await foreignDownload.Content.ReadAsByteArrayAsync(ct);
        Assert.False(leaked.AsSpan().IndexOf(a.ArtifactBytes) >= 0, "Organization A's artifact bytes leaked to organization B.");
    }

    private sealed record Tenant(
        int OrganizationId, int ProjectId, int ServerId, int VaultId, int PipelineId, int RunId,
        int ArtifactId, byte[] ArtifactBytes, string Marker);

    /// <summary>One organization with a project, a server, a vault, a pipeline, a finished run and its artifact.</summary>
    private static async Task<Tenant> SeedTenantAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IArtifactStorageService>();
        var marker = $"tenant-{label}-{Guid.NewGuid():N}";

        var organization = new Organization { Name = marker, Slug = $"t{label}{Guid.NewGuid():N}"[..20] };
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
        var vault = new Vault { Name = $"{marker}-vault", Description = marker, ProjectId = project.Id };
        var pipeline = new Pipeline { Name = $"{marker}-pipeline", ProjectId = project.Id, YamlDefinition = "name: t\nstages: []" };
        db.Vaults.Add(vault);
        db.Pipelines.Add(pipeline);
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

        var bytes = Encoding.UTF8.GetBytes($"{marker}-artifact-bytes");
        var (relativePath, sha256) = await storage.SaveArtifactAsync(
            project.Id, pipeline.Id, run.Id, "drop.zip", new MemoryStream(bytes), ct);
        var artifact = new PipelineArtifact
        {
            PipelineRunId = run.Id,
            PipelineId = pipeline.Id,
            ProjectId = project.Id,
            Name = $"{marker}-drop",
            FilePath = relativePath,
            SizeBytes = bytes.Length,
            Sha256 = sha256,
            CreatedAt = DateTime.UtcNow,
            RetentionExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        db.PipelineArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);

        return new Tenant(organization.Id, project.Id, server.Id, vault.Id, pipeline.Id, run.Id, artifact.Id, bytes, marker);
    }
}
