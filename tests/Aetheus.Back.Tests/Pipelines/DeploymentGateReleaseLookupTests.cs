// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The release whose grade the deployment gate reads is the one of the run's own project,
/// picked as the restore-artifacts step picks it (numeric = id, else newest exact
/// version).</summary>
public sealed class DeploymentGateReleaseLookupTests : IDisposable
{
    private static readonly DateTime Origin = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void AddRelease(int id, int projectId, string version, AnalysisGrade? grade, int ageDays) =>
        _db.Releases.Add(new Release
        {
            Id = id,
            ProjectId = projectId,
            Version = version,
            AssuranceGrade = grade,
            DetectedAt = Origin.AddDays(-ageDays)
        });

    [Fact]
    public async Task AVersion_ResolvesInsideTheProject_NewestFirst()
    {
        AddRelease(1, projectId: 3, "c-abc", AnalysisGrade.A, ageDays: 5);
        AddRelease(2, projectId: 3, "c-abc", AnalysisGrade.F, ageDays: 1);
        AddRelease(3, projectId: 4, "c-abc", AnalysisGrade.A, ageDays: 0);
        await _db.SaveChangesAsync(Ct);

        var release = await new PipelineServerResolver(_db).FindDeploymentGateReleaseAsync(3, "c-abc", Ct);

        Assert.Equal(new DeploymentGateRelease(2, "c-abc", AnalysisGrade.F), release);
    }

    [Fact]
    public async Task ANumericSelector_IsAReleaseId_AndAnotherProjectsReleaseIsNotFound()
    {
        AddRelease(7, projectId: 3, "1.2.3", null, ageDays: 1);
        AddRelease(8, projectId: 4, "1.2.4", AnalysisGrade.A, ageDays: 1);
        await _db.SaveChangesAsync(Ct);
        var resolver = new PipelineServerResolver(_db);

        Assert.Equal(new DeploymentGateRelease(7, "1.2.3", null), await resolver.FindDeploymentGateReleaseAsync(3, "7", Ct));
        Assert.Null(await resolver.FindDeploymentGateReleaseAsync(3, "8", Ct));
        Assert.Null(await resolver.FindDeploymentGateReleaseAsync(3, "c-missing", Ct));
    }

    public void Dispose() => _db.Dispose();
}
