// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Projects;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The images and the patch state of the servers of a project, on PostgreSQL (PLAN-004 lot 5, points 7 to 9). The only link
/// between a server and a project is <c>ProjectServer</c>, and it is used as it is: a server shared by two projects gives its
/// images to neither, since nothing says which of them runs them. Everything goes through the real inventory service, the
/// real repository and the real matching; no Git repository is involved. What this does not prove: the agent reporting the
/// images and the patch state (rows are seeded as the heartbeat writes them).
/// </summary>
public sealed class ProjectServerInventoryIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Now));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task RefreshAsync(int projectId)
    {
        await using var db = NewContext();
        var options = Options.Create(new VulnerabilityWatchOptions());
        var reader = new RepositoryInventoryReader(
            new GitProjectSourceReader(new GitLightRepository(db), Substitute.For<IGitLightCliService>(), Substitute.For<IGitLightService>()),
            options, NullLogger<RepositoryInventoryReader>.Instance);
        var matching = new VulnerabilityMatchService(new VulnerabilityMatchRepository(db), _clock, NullLogger<VulnerabilityMatchService>.Instance);
        await new ProjectInventoryService(
            new ProjectInventoryRepository(db), reader, new ProjectInventoryGate(), matching, options, _clock,
            NullLogger<ProjectInventoryService>.Instance).RefreshProjectAsync(projectId, Ct);
    }

    private async Task<(int OrganizationId, int ProjectId)> SeedProjectAsync(string name, int? organizationId = null)
    {
        await using var db = NewContext();
        var organization = organizationId is { } id
            ? await db.Organizations.SingleAsync(row => row.Id == id, Ct)
            : db.Organizations.Add(new Organization { Name = name, Slug = name.ToLowerInvariant(), Description = "d" }).Entity;
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = name, Description = "d", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        return (organization.Id, project.Id);
    }

    private async Task<int> SeedServerAsync(int organizationId, string name, params (string Repository, string Tag)[] images)
    {
        await using var db = NewContext();
        var server = new Server { Name = name, Hostname = name, OrganizationId = organizationId };
        db.Servers.Add(server);
        await db.SaveChangesAsync(Ct);
        db.DockerImages.AddRange(images.Select((image, index) => new DockerImage
        {
            ServerId = server.Id,
            ImageId = $"sha256:{name}{index}",
            Repository = image.Repository,
            Tag = image.Tag,
            Size = "1MB",
            Created = Now,
            LastUpdated = Now
        }));
        await db.SaveChangesAsync(Ct);
        return server.Id;
    }

    private async Task LinkAsync(int projectId, int? serverId)
    {
        await using var db = NewContext();
        db.ProjectServers.Add(new ProjectServer
        {
            ProjectId = projectId,
            ServerId = serverId,
            Type = serverId is null ? ProjectServerType.ExternalHost : ProjectServerType.AgentServer,
            DisplayName = $"link-{projectId}-{serverId}",
            Host = "host"
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<ProjectInventory?> ServerInventoryAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.ProjectInventories.AsNoTracking().Include(inventory => inventory.Items)
            .SingleOrDefaultAsync(inventory => inventory.ProjectId == projectId && inventory.Origin == ProjectInventoryOrigin.Server, Ct);
    }

    [Fact]
    public async Task TheImagesOfAServerLinkedToOneProject_AreItsInventory_PartialSinceNoSbomSaysWhatTheyHold()
    {
        await ResetAndMigrateAsync();
        var project = await SeedProjectAsync("Alpha");
        var other = await SeedProjectAsync("Beta");
        var server = await SeedServerAsync(project.OrganizationId, "srv", ("library/node", "20.5.0-alpine"), ("mcr.microsoft.com/dotnet/aspnet", "10.0.13"), ("postgres", "16"), ("<none>", "<none>"));
        await LinkAsync(project.ProjectId, server);

        await RefreshAsync(project.ProjectId);
        await RefreshAsync(other.ProjectId);

        var inventory = await ServerInventoryAsync(project.ProjectId);
        Assert.NotNull(inventory);
        Assert.Equal(ProjectInventoryCoverage.Partial, inventory.Coverage);
        Assert.Equal(
            ["docker:mcr.microsoft.com/dotnet/aspnet@10.0.13", "docker:node@20.5.0-alpine", "docker:postgres@16"],
            inventory.Items.Select(item => $"{item.Ecosystem}:{item.PackageName}@{item.Version}").Order());
        Assert.All(inventory.Items, item => Assert.Equal(ProjectInventoryItemKind.BaseImage, item.Kind));
        Assert.Equal("no-sbom:docker=3, untagged-image=1", inventory.Reason);
        // A project that no server is linked to has nothing to say at this origin: no row, never an empty "clean" one.
        Assert.Null(await ServerInventoryAsync(other.ProjectId));
    }

    [Fact]
    public async Task AServerSharedByTwoProjects_GivesItsImagesToNeither_AndSaysWhy()
    {
        await ResetAndMigrateAsync();
        var first = await SeedProjectAsync("First");
        var second = await SeedProjectAsync("Second", first.OrganizationId);
        var server = await SeedServerAsync(first.OrganizationId, "shared", ("node", "20.5.0"));
        await LinkAsync(first.ProjectId, server);
        await LinkAsync(second.ProjectId, server);

        await RefreshAsync(first.ProjectId);
        await RefreshAsync(second.ProjectId);

        foreach (var project in new[] { first, second })
        {
            var inventory = await ServerInventoryAsync(project.ProjectId);
            Assert.NotNull(inventory);
            Assert.Equal(ProjectInventoryCoverage.None, inventory.Coverage);
            Assert.Empty(inventory.Items);
            Assert.Equal("shared-server=1", inventory.Reason);
        }
    }

    [Fact]
    public async Task AProjectServerThatNamesNoRegisteredServer_IsAGap_NotACleanServer()
    {
        await ResetAndMigrateAsync();
        var project = await SeedProjectAsync("External");
        await LinkAsync(project.ProjectId, null);

        await RefreshAsync(project.ProjectId);

        var inventory = await ServerInventoryAsync(project.ProjectId);
        Assert.NotNull(inventory);
        Assert.Equal(ProjectInventoryCoverage.None, inventory.Coverage);
        Assert.Equal("server-not-registered=1", inventory.Reason);
    }

    [Fact]
    public async Task AnImageOfTheServerBehindItsRuntimesFix_IsMatched_AndLeavingTheServerRemovesTheInventory()
    {
        await ResetAndMigrateAsync();
        var project = await SeedProjectAsync("Alpha");
        var server = await SeedServerAsync(project.OrganizationId, "srv", ("docker.io/library/node", "20.5.0-alpine"));
        await LinkAsync(project.ProjectId, server);
        await using (var db = NewContext())
        {
            var repository = new VulnerabilityFeedRepository(db);
            var record = RuntimeAdvisoryFactory.Create(
                RuntimeProducts.Node, "NODEJS-SECURITY-20", null, [], "Node.js 20: security release",
                (RuntimeAdvisoryFactory.Day(new DateOnly(2026, 10, 1)), RuntimeAdvisoryFactory.Day(new DateOnly(2026, 10, 1))),
                [new RuntimeRange("20.0.0-alpha", "20.20.2")]);
            await repository.ApplyBatchAsync([record], await repository.GetOrCreateStateAsync("runtime:nodejs", Ct), Ct);
        }

        await RefreshAsync(project.ProjectId);

        await using (var db = NewContext())
        {
            var match = await db.VulnerabilityMatches.AsNoTracking().Include(row => row.Advisory)
                .SingleAsync(row => row.ProjectId == project.ProjectId, Ct);
            Assert.Equal("NODEJS-SECURITY-20", match.Advisory.ExternalId);
            Assert.Equal("node", match.PackageName);
            Assert.Equal(VulnerabilityMatchStatus.Affected, match.Status);
        }

        await using (var db = NewContext())
        {
            db.ProjectServers.RemoveRange(db.ProjectServers.Where(link => link.ProjectId == project.ProjectId));
            await db.SaveChangesAsync(Ct);
        }

        await RefreshAsync(project.ProjectId);
        Assert.Null(await ServerInventoryAsync(project.ProjectId));
    }

    private async Task SeedUpdatesAsync(int serverId, bool probed, int pendingSecurity, int pendingTotal)
    {
        await using var db = NewContext();
        db.SecurityUpdatesStates.Add(new SecurityUpdatesState
        {
            ServerId = serverId,
            Probed = probed,
            PendingSecurity = pendingSecurity,
            PendingTotal = pendingTotal,
            CheckedAt = Now,
            LastUpdated = Now
        });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ThePatchStateOfTheServersOfAProject_IsReportedPerProject_AnUnknownStateNeverReadsAsZero()
    {
        await ResetAndMigrateAsync();
        var project = await SeedProjectAsync("Alpha");
        var other = await SeedProjectAsync("Beta", project.OrganizationId);
        var behind = await SeedServerAsync(project.OrganizationId, "behind");
        var current = await SeedServerAsync(project.OrganizationId, "current");
        var unprobed = await SeedServerAsync(project.OrganizationId, "unprobed");
        var silent = await SeedServerAsync(project.OrganizationId, "silent");
        var foreign = await SeedServerAsync(project.OrganizationId, "foreign");
        await SeedUpdatesAsync(behind, probed: true, pendingSecurity: 3, pendingTotal: 10);
        await SeedUpdatesAsync(current, probed: true, pendingSecurity: 0, pendingTotal: 0);
        await SeedUpdatesAsync(unprobed, probed: false, pendingSecurity: 0, pendingTotal: 0);
        await SeedUpdatesAsync(foreign, probed: true, pendingSecurity: 99, pendingTotal: 99);
        foreach (var server in new[] { behind, current, unprobed, silent }) await LinkAsync(project.ProjectId, server);
        await LinkAsync(project.ProjectId, null);
        await LinkAsync(other.ProjectId, foreign);
        // A server shared with another project is still the host of this one: its patch state concerns both.
        await LinkAsync(other.ProjectId, behind);

        await using var db = NewContext();
        var report = await new ProjectSecurityUpdatesRepository(db).GetAsync(project.ProjectId, Ct);

        Assert.Equal(
            [
                ("behind", ServerUpdatesState.Pending, 3, 10),
                ("current", ServerUpdatesState.UpToDate, 0, 0),
                ("silent", ServerUpdatesState.NotReported, 0, 0),
                ("unprobed", ServerUpdatesState.Unknown, 0, 0)
            ],
            report.Servers.OrderBy(server => server.ServerName, StringComparer.Ordinal)
                .Select(server => (server.ServerName, server.State, server.PendingSecurity, server.PendingTotal)));
        Assert.Equal(3, report.PendingSecurity);
        Assert.Equal(2, report.UnknownServers);
    }
}
