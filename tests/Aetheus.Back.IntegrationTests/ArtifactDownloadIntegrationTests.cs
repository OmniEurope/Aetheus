// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// End-to-end coverage for <c>GET /api/artifacts/{id}/download</c> through the full pipeline
/// (auth → controller → service → on-disk storage) against Testcontainers PostgreSQL: a real
/// artifact file is written via <see cref="IArtifactStorageService"/>, then streamed back and its
/// bytes asserted. The InMemory suite can't exercise the file streaming or the real auth flow.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ArtifactDownloadIntegrationTests(PostgresFixture fixture)
{
    private static Task<string> LoginAsync(HttpClient client)
        => IntegrationAuth.LoginAsAdminAsync(client, TestContext.Current.CancellationToken);

    [Fact]
    public async Task DownloadArtifact_StreamsStoredFileContent()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client));

        var payload = Encoding.UTF8.GetBytes("artifact-bytes-payload");
        int artifactId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IArtifactStorageService>();

            // Gives the pipeline a real owning project because artifact authorization resolves
            // ownership transitively and refuses unresolved owners.
            var organizationId = await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id)
                .FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var project = new Project { Name = "dl-project", OrganizationId = organizationId };
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var pipeline = new Pipeline { Name = "dl-pipeline", ProjectId = project.Id, YamlDefinition = "stages: []" };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var (relativePath, sha256) = await storage.SaveArtifactAsync(1, pipeline.Id, run.Id, "drop.zip", new MemoryStream(payload), ct: TestContext.Current.CancellationToken);

            var artifact = new PipelineArtifact
            {
                PipelineRunId = run.Id,
                PipelineId = pipeline.Id,
                ProjectId = null, // realistic: the artifact's own column is null, the owner comes from the pipeline
                Name = "drop",
                FilePath = relativePath,
                SizeBytes = payload.Length,
                Sha256 = sha256,
                CreatedAt = DateTime.UtcNow,
                RetentionExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.PipelineArtifacts.Add(artifact);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            artifactId = artifact.Id;
        }

        var response = await client.GetAsync($"/api/artifacts/{artifactId}/download", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Recette R1-91: a chunked upload is published by moving its verified assembled file
    /// into the store. The row, the stored bytes and the download must still be exactly the
    /// announced artifact, against real PostgreSQL and the real file system.</summary>
    [Fact]
    public async Task ChunkedUpload_PublishedByAdoption_IsDownloadedByteForByte()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client));

        var first = Encoding.UTF8.GetBytes("first part of the payload|");
        var second = Encoding.UTF8.GetBytes("second part of the payload");
        var whole = first.Concat(second).ToArray();
        static string Sha(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        PipelineArtifactDto? published;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var organizationId = await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id).FirstAsync(ct);
            var project = new Project { Name = "chunked-project", OrganizationId = organizationId };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            var pipeline = new Pipeline { Name = "chunked-pipeline", ProjectId = project.Id, YamlDefinition = "stages: []" };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(ct);
            var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(ct);

            var uploads = scope.ServiceProvider.GetRequiredService<IChunkedArtifactUploadService>();
            var artifacts = scope.ServiceProvider.GetRequiredService<IArtifactService>();
            var session = await uploads.BeginAsync(run.Id, "payload", "Build", 2, Sha(whole), ct);
            Assert.True((await uploads.AcceptPartAsync(run.Id, session.UploadId, 0, Sha(first), new MemoryStream(first), ct)).Accepted);
            Assert.True((await uploads.AcceptPartAsync(run.Id, session.UploadId, 1, Sha(second), new MemoryStream(second), ct)).Accepted);

            published = await uploads.CompleteAsync(run.Id, session.UploadId,
                verified => artifacts.PublishVerifiedArtifactAsync(run.Id, "payload", "Build", verified, ct), ct);
        }

        Assert.NotNull(published);
        Assert.Equal(Sha(whole), published.Sha256);
        Assert.Equal(whole.Length, published.SizeBytes);
        var response = await client.GetAsync($"/api/artifacts/{published.Id}/download", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(whole, await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task DownloadArtifact_MissingArtifact_Returns404()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client));

        var response = await client.GetAsync("/api/artifacts/999999/download", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
