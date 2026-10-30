// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R1-51: deactivating a pipeline owner (<c>PUT /api/users/{id}</c> with a transfer target) on
/// the real host and PostgreSQL. The dialog has already shown the preview; the transfer it triggers must
/// change the owners and audit them without preparing every pipeline again. Preparations are counted
/// at the service boundary and the PUT is timed for 1, 10 and 20 pipelines (written to the test output).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserDeactivationPipelineTransferIntegrationTests(PostgresFixture fixture)
{
    private const string Yaml =
        "name: handover\n" +
        "trigger: manual\n" +
        "stages:\n" +
        "  - name: build\n" +
        "    agent: any\n" +
        "    steps:\n" +
        "      - name: echo\n" +
        "        shell: echo hi\n";

    [Fact]
    public async Task Deactivation_WithTransfer_MovesEveryPipeline_WithoutPreparingThemAgain()
    {
        var counter = new PreparationCounter();
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(counter);
            services.AddScoped<PipelineRunPreparationService>();
            services.AddScoped<IPipelineRunPreparationService>(sp => new CountingPreparationService(
                sp.GetRequiredService<PipelineRunPreparationService>(), sp.GetRequiredService<PreparationCounter>()));
        }));
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var target = await CreateUserAsync(admin, "handover-target");

        // Warm-up round (JIT, connection pool), not reported.
        await DeactivateOwnerOfAsync(factory, admin, counter, target.Username, 1);

        var preparedPerRound = new List<int>();
        foreach (var count in new[] { 1, 10, 20 })
        {
            var (previewMs, putMs, preparedDuringPut) = await DeactivateOwnerOfAsync(
                factory, admin, counter, target.Username, count);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"R1-51 pipelines={count} previewMs={previewMs} putMs={putMs} preparationsDuringPut={preparedDuringPut}");
            preparedPerRound.Add(preparedDuringPut);
        }
        Assert.All(preparedPerRound, prepared => Assert.Equal(0, prepared));
    }

    /// <summary>Creates an owner of <paramref name="count"/> pipelines, previews the transfer as the
    /// dialog does, then deactivates the owner with the transfer, and checks what the PUT changed.</summary>
    private static async Task<(long PreviewMs, long PutMs, int PreparedDuringPut)> DeactivateOwnerOfAsync(
        WebApplicationFactory<Program> factory, HttpClient admin, PreparationCounter counter, string targetUsername, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await CreateUserAsync(admin, "handover-owner");
        var pipelineIds = await SeedPipelinesAsync(factory, owner.Username, count);

        var watch = Stopwatch.StartNew();
        var preview = await admin.PostAsJsonAsync("/api/pipelines/ownership/preview", new PipelineOwnerTransferRequest
        {
            CurrentOwnerUsername = owner.Username,
            NewOwnerUsername = targetUsername
        }, ct);
        var previewMs = watch.ElapsedMilliseconds;
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        var before = counter.Count;
        watch.Restart();
        var put = await admin.PutAsJsonAsync($"/api/users/{owner.Id}", new UpdateUserRequest
        {
            Username = owner.Username,
            Email = $"{owner.Username}@example.com",
            IsActive = false,
            Roles = [], // ADR-059: only Admin is site-wide; the Reader role it holds per organization is left unchanged.
            TransferPipelinesToUsername = targetUsername
        }, ct);
        var putMs = watch.ElapsedMilliseconds;
        var preparedDuringPut = counter.Count - before;
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owners = await db.Pipelines.AsNoTracking()
            .Where(p => pipelineIds.Contains(p.Id)).Select(p => p.CreatedByUsername).ToListAsync(ct);
        Assert.All(owners, o => Assert.Equal(targetUsername, o));
        var audited = await db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Action == PipelineOwnershipService.TransferredAuditAction
                && a.EntityType == "Pipeline" && a.EntityId != null && pipelineIds.Contains(a.EntityId.Value), ct);
        Assert.Equal(count, audited);
        Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == owner.Id, ct)).IsActive);
        return (previewMs, putMs, preparedDuringPut);
    }

    private static async Task<(int Id, string Username)> CreateUserAsync(HttpClient admin, string prefix)
    {
        var ct = TestContext.Current.CancellationToken;
        var username = $"{prefix}-{Guid.NewGuid():N}"[..40];
        var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = username,
            Email = $"{username}@example.com",
            OrganizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(admin, "Reader")
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct);
        return (user!.Id, user.Username);
    }

    private static async Task<List<int>> SeedPipelinesAsync(WebApplicationFactory<Program> factory, string owner, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipelines = Enumerable.Range(0, count).Select(i => new Pipeline
        {
            Name = $"handover-{Guid.NewGuid():N}",
            YamlDefinition = Yaml,
            CreatedByUsername = owner
        }).ToList();
        db.Pipelines.AddRange(pipelines);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return pipelines.Select(p => p.Id).ToList();
    }

    private sealed class PreparationCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>Counts the preparations and delegates them to the real service.</summary>
    private sealed class CountingPreparationService(IPipelineRunPreparationService inner, PreparationCounter counter)
        : IPipelineRunPreparationService
    {
        public Task<PipelineRunPreparation?> PrepareRunAsync(
            int pipelineId, string? sourceBranch, string? yamlOverride, string? commitOverride, CancellationToken ct,
            string? workspaceCommitOverride = null)
        {
            counter.Increment();
            return inner.PrepareRunAsync(pipelineId, sourceBranch, yamlOverride, commitOverride, ct, workspaceCommitOverride);
        }

        public Task<List<PipelineRunParameterDto>> GetRunParametersAsync(
            int pipelineId, string? sourceBranch = null, CancellationToken ct = default) =>
            inner.GetRunParametersAsync(pipelineId, sourceBranch, ct);
    }
}
