// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Neighbour of BACK-018 on a real PostgreSQL: a stage's <c>environment: prod</c> picks its servers in
/// the run's project (own environment first, then a shared one), never in another project's homonym of
/// the same organization. The organization filter alone let the first project's prod host serve the
/// second project's stage, for runners, deployment targets and the authorization candidate set alike.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineServerEnvironmentScopeIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task AStageOfTheSecondProject_NeverTargetsTheFirstProjectsProdServers()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(ct);

        await using var db = NewContext();
        var resolver = new PipelineServerResolver(db);

        Assert.Equal([seeded.SecondServer], await resolver.FindCandidateTargetServerIdsAsync(
            null, "prod", null, OsType.Unknown, seeded.OrganizationId, deploymentStage: false, seeded.SecondProject, ct));
        Assert.Equal([seeded.SecondServer], await resolver.FindCandidateTargetServerIdsAsync(
            null, "prod", null, OsType.Unknown, seeded.OrganizationId, deploymentStage: true, seeded.SecondProject, ct));
        Assert.Equal([seeded.FirstServer], await resolver.FindCandidateTargetServerIdsAsync(
            null, "prod", null, OsType.Unknown, seeded.OrganizationId, deploymentStage: false, seeded.FirstProject, ct));
        Assert.Equal([seeded.SecondServer], await resolver.FindServerIdsInEnvironmentAsync("prod", seeded.SecondProject, ct));

        // The online lookups dispatch reads: same answer, whichever homonym was written first.
        Assert.Equal(seeded.SecondServer, (await resolver.FindOnlineServerInEnvironmentInOrganizationAsync(
            "prod", OsType.Unknown, seeded.OrganizationId, seeded.SecondProject, ct))?.Id);
        Assert.Equal(seeded.SecondServer, (await resolver.FindOnlineServerInEnvironmentAsync(
            "prod", seeded.SecondProject, OsType.Unknown, ct))?.Id);
        Assert.Equal(seeded.SecondServer, (await resolver.FindOnlineDeployTargetAsync(
            null, "prod", null, OsType.Unknown, seeded.OrganizationId, seeded.SecondProject, ct))?.Id);
    }

    [Fact]
    public async Task AProjectWithoutItsOwnEnvironment_UsesTheSharedOne_NeverAnotherProjects()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(ct);

        await using var db = NewContext();
        var resolver = new PipelineServerResolver(db);

        // "qa" exists shared and in the first project: the second project gets the shared one.
        Assert.Equal([seeded.SharedServer], await resolver.FindCandidateTargetServerIdsAsync(
            null, "qa", null, OsType.Unknown, seeded.OrganizationId, deploymentStage: false, seeded.SecondProject, ct));
        // The first project's own qa wins over the shared one for the first project.
        Assert.Equal([seeded.FirstServer], await resolver.FindCandidateTargetServerIdsAsync(
            null, "qa", null, OsType.Unknown, seeded.OrganizationId, deploymentStage: false, seeded.FirstProject, ct));
        // "prod" exists only in projects: a run with no project sees none of them.
        Assert.Empty(await resolver.FindCandidateTargetServerIdsAsync(
            null, "prod", null, OsType.Unknown, seeded.OrganizationId, deploymentStage: false, projectId: null, ct));
        Assert.Null(await resolver.FindOnlineDeployTargetAsync(
            null, "prod", null, OsType.Unknown, seeded.OrganizationId, projectId: null, ct));
    }

    private sealed record Seeded(
        int OrganizationId, int FirstProject, int SecondProject, int FirstServer, int SecondServer, int SharedServer);

    private async Task<Seeded> SeedAsync(CancellationToken ct)
    {
        await using var seed = NewContext();
        var organization = new Organization { Name = "Fleet", Slug = $"fleet-{Guid.NewGuid():N}"[..20] };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(ct);
        var first = new Project { Name = "First", OrganizationId = organization.Id };
        var second = new Project { Name = "Second", OrganizationId = organization.Id };
        seed.Projects.AddRange(first, second);
        var firstHost = NewServer(organization.Id, "first-prod");
        var secondHost = NewServer(organization.Id, "second-prod");
        var sharedHost = NewServer(organization.Id, "shared-qa");
        seed.Servers.AddRange(firstHost, secondHost, sharedHost);
        await seed.SaveChangesAsync(ct);
        // The first project's environments are written first, so an unscoped lookup meets them first.
        var firstProd = new Environment { Name = "prod", ProjectId = first.Id };
        var firstQa = new Environment { Name = "qa", ProjectId = first.Id };
        seed.Environments.AddRange(firstProd, firstQa);
        await seed.SaveChangesAsync(ct);
        var secondProd = new Environment { Name = "prod", ProjectId = second.Id };
        var sharedQa = new Environment { Name = "qa" };
        seed.Environments.AddRange(secondProd, sharedQa);
        await seed.SaveChangesAsync(ct);
        seed.EnvironmentServers.AddRange(
            new EnvironmentServer { EnvironmentId = firstProd.Id, ServerId = firstHost.Id },
            new EnvironmentServer { EnvironmentId = firstQa.Id, ServerId = firstHost.Id },
            new EnvironmentServer { EnvironmentId = secondProd.Id, ServerId = secondHost.Id },
            new EnvironmentServer { EnvironmentId = sharedQa.Id, ServerId = sharedHost.Id });
        await seed.SaveChangesAsync(ct);
        return new Seeded(organization.Id, first.Id, second.Id, firstHost.Id, secondHost.Id, sharedHost.Id);
    }

    private static Server NewServer(int organizationId, string name) => new()
    {
        OrganizationId = organizationId,
        Name = name,
        Hostname = $"{name}.local",
        Status = ServerStatus.Online,
        PipelineRunnerEnabled = true,
        DeploymentTargetAvailable = true,
        AgentProtocolVersion = AgentProtocol.MaximumSupportedVersion,
        AgentCapabilitiesJson = $"[\"{AgentCapabilities.PipelineBuild}\",\"{AgentCapabilities.Deployment}\"]",
    };
}
