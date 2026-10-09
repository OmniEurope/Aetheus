// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Audit BACK-018: an environment name is unique per project only, yet the approval gate and the
/// environment-check guard read "prod" by name across every project and took the first one found. Two
/// projects each with a prod, the first (older) without approval or check, the second requiring both:
/// a run of the second project was gated by the first one's empty policy. Real repository, so the
/// lookup itself is what is exercised.
/// </summary>
public sealed class PipelineEnvironmentProjectScopeTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineEnvironmentProjectScopeTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    private sealed record Seeded(int FirstProjectId, int SecondProjectId, int FirstProdId, int SecondProdId, int SecondRunId);

    private async Task<Seeded> SeedTwoProjectsWithProdAsync(CancellationToken ct)
    {
        var organization = new Organization { Name = "Org", Slug = "org" };
        _db.Organizations.Add(organization);
        await _db.SaveChangesAsync(ct);
        var first = new Project { Name = "First", OrganizationId = organization.Id };
        var second = new Project { Name = "Second", OrganizationId = organization.Id };
        _db.Projects.AddRange(first, second);
        await _db.SaveChangesAsync(ct);

        // Created first, so the lowest id: the one a global lookup by name returned.
        var firstProd = new Environment { Name = "prod", ProjectId = first.Id };
        _db.Environments.Add(firstProd);
        await _db.SaveChangesAsync(ct);
        var secondProd = new Environment { Name = "prod", ProjectId = second.Id, RequireApproval = true };
        _db.Environments.Add(secondProd);
        await _db.SaveChangesAsync(ct);
        // A required REST callback to a loopback host: refused by the SSRF guard, no network involved.
        _db.Set<EnvironmentCheck>().Add(new EnvironmentCheck
        {
            EnvironmentId = secondProd.Id,
            Name = "change window",
            Type = EnvironmentCheckType.RestCallback,
            Configuration = """{"url":"http://localhost/refuse"}""",
            IsRequired = true
        });
        var pipeline = new Pipeline { Name = "deliver", ProjectId = second.Id, YamlDefinition = "name: deliver" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(ct);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return new Seeded(first.Id, second.Id, firstProd.Id, secondProd.Id, run.Id);
    }

    [Fact]
    public async Task ARunOfTheSecondProject_AsksTheApprovalOfItsOwnProd()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedTwoProjectsWithProdAsync(ct);
        var gate = new PipelineStageApprovalGate(
            _repo, Substitute.For<IHubContext<PipelineHub>>(), Substitute.For<IDomainEventDispatcher>(), TimeProvider.System);
        var deploy = new PipelineStageDefinition { Name = "Deploy", Environment = "prod" };

        var projectId = await _repo.GetRunProjectIdAsync(seeded.SecondRunId, ct);
        var held = await gate.CheckAndCreateApprovalAsync(seeded.SecondRunId, projectId, "Deploy", deploy, [deploy], ct);

        Assert.Equal(seeded.SecondProjectId, projectId);
        Assert.True(held);
        var approval = Assert.Single(await _db.PipelineApprovals.AsNoTracking().ToListAsync(ct));
        Assert.Equal(seeded.SecondProdId, approval.EnvironmentId);
        var run = await _db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == seeded.SecondRunId, ct);
        Assert.Equal(PipelineStatus.WaitingForApproval, run.Status);
    }

    [Fact]
    public async Task TheChecksOfTheRunsOwnProd_AreTheOnesEvaluated()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedTwoProjectsWithProdAsync(ct);
        var guard = new PipelineEnvironmentCheckGuard(
            _repo, Substitute.For<IHttpClientFactory>(), NullLogger<PipelineEnvironmentCheckGuard>.Instance);
        var deploy = new PipelineStageDefinition { Name = "Deploy", Environment = "prod" };

        Assert.False(await guard.CheckEnvironmentChecksAsync(deploy, seeded.SecondProjectId, ct));
        Assert.True(await guard.CheckEnvironmentChecksAsync(deploy, seeded.FirstProjectId, ct));
    }

    [Fact]
    public async Task TheLookup_PrefersTheProjectsOwn_FallsBackToAShared_AndNeverTakesAnotherProjects()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedTwoProjectsWithProdAsync(ct);
        _db.Environments.Add(new Environment { Name = "prod" });
        _db.Environments.Add(new Environment { Name = "staging", ProjectId = seeded.FirstProjectId });
        _db.Environments.Add(new Environment { Name = "shared-qa" });
        await _db.SaveChangesAsync(ct);

        Assert.Equal(seeded.SecondProdId, (await _repo.FindEnvironmentByNameAsync("prod", seeded.SecondProjectId, ct))?.Id);
        Assert.Equal(seeded.FirstProdId, (await _repo.FindEnvironmentByNameAsync("prod", seeded.FirstProjectId, ct))?.Id);
        Assert.Null((await _repo.FindEnvironmentByNameAsync("prod", null, ct))?.ProjectId);
        Assert.NotNull(await _repo.FindEnvironmentByNameAsync("shared-qa", seeded.SecondProjectId, ct));
        Assert.Null(await _repo.FindEnvironmentByNameAsync("staging", seeded.SecondProjectId, ct));
    }
}
