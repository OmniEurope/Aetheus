// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The reads behind "used by" (R-65, R-66): a resource owned through an environment is
/// attributed to the environment's project, a pipeline likewise, and the readable-id filter holds.</summary>
public sealed class PipelineResourceUsageRepositoryTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new FakeTimeProvider());

    private readonly PipelineResourceUsageRepository _sut;

    public PipelineResourceUsageRepositoryTests()
    {
        _sut = new PipelineResourceUsageRepository(_db);
        _db.Projects.Add(new Project { Id = 7, Name = "Aetheus", OrganizationId = 3 });
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 70, Name = "prod", ProjectId = 7 });
        _db.VariableLibraries.AddRange(
            new VariableLibrary { Id = 1, Name = "direct", ProjectId = 7 },
            new VariableLibrary { Id = 2, Name = "through-environment", EnvironmentId = 70 });
        _db.Vaults.Add(new Vault { Id = 5, Name = "vault", ProjectId = 7 });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 10, Name = "direct", ProjectId = 7, YamlDefinition = "name: a" },
            new Pipeline { Id = 11, Name = "through-environment", EnvironmentId = 70, YamlDefinition = "name: b" });
        _db.MonitoredApps.AddRange(
            new MonitoredApp { Id = 40, ProjectId = 7, Name = "Site", AnalyticsVaultName = "aetheus.analytics" },
            new MonitoredApp { Id = 41, ProjectId = 7, Name = "No analytics" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ALibraryOrVaultIsAttributedToItsOwningProject_AndTheReadableIdsFilter()
    {
        var libraries = await _sut.GetLibrariesAsync(null, Ct);
        Assert.Equal([(1, 7), (2, 7)], libraries.Select(library => (library.Id, library.OwnerProjectId!.Value)));

        var onlyOne = await _sut.GetLibrariesAsync([2], Ct);
        Assert.Equal(2, Assert.Single(onlyOne).Id);
        Assert.Empty(await _sut.GetVaultsAsync([], Ct));
        Assert.Equal(7, Assert.Single(await _sut.GetVaultsAsync(null, Ct)).OwnerProjectId);
    }

    [Fact]
    public async Task APipelineCarriesItsProjectNameAndOrganization_AlsoThroughItsEnvironment()
    {
        var pipelines = await _sut.GetPipelinesAsync(Ct);

        Assert.Equal(2, pipelines.Count);
        Assert.All(pipelines, pipeline =>
        {
            Assert.Equal(7, pipeline.ProjectId);
            Assert.Equal("Aetheus", pipeline.ProjectName);
            Assert.Equal(3, pipeline.OrganizationId);
        });
    }

    [Fact]
    public async Task OnlyAnApplicationWithAnAnalyticsVaultIsListed()
    {
        var app = Assert.Single(await _sut.GetAnalyticsAppsAsync(Ct));

        Assert.Equal((40, "Site", 7, "aetheus.analytics"), (app.Id, app.Name, app.ProjectId, app.VaultName));
    }
}
