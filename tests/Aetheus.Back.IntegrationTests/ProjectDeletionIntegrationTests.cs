// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Project deletion removes owned rows under actual foreign-key rules while preserving
/// shared servers and unrelated project data.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectDeletionIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture), IDisposable
{
    /// <summary>More task log lines than one batch holds, so the batched delete loops.</summary>
    private const int ManyLogLines = ProjectDeletionRepository.BatchSize + 7;

    private readonly string _storageRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "aetheus-project-deletion-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IGitLightCliService _gitCli = Substitute.For<IGitLightCliService>();

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true);
    }

    [Fact]
    public async Task Delete_RemovesEverythingTheProjectOwns_AndLeavesTheOtherProjectAndTheServerIntact()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        SeededProject deleted, kept;
        int serverId;
        await using (var db = NewContext())
        {
            var (organization, server, user, role) = await SeedSharedAsync(db, ct);
            serverId = server.Id;
            deleted = await SeedProjectAsync(db, "Portfolio", organization, server, user, role, ManyLogLines, 9001, ct);
            kept = await SeedProjectAsync(db, "Atlas", organization, server, user, role, 3, 9002, ct);
        }

        await using (var db = NewContext())
        {
            var preview = await BuildService(db).GetPreviewAsync(deleted.ProjectId, ct);
            Assert.NotNull(preview);
            Assert.Equal(0, preview.ActiveRunCount);
            var counts = preview.Items.ToDictionary(item => item.Kind, item => item.Count);
            Assert.Equal(2, counts[ProjectDeletionItemKind.Pipelines]);
            Assert.Equal(ManyLogLines, counts[ProjectDeletionItemKind.TaskLogLines]);
            Assert.Equal(3, counts[ProjectDeletionItemKind.Vaults]);
            Assert.Equal(2, counts[ProjectDeletionItemKind.TelemetryRecords]);
            Assert.Equal(1, counts[ProjectDeletionItemKind.PortReservations]);
        }

        await using (var db = NewContext())
            Assert.True(await BuildService(db).DeleteAsync(deleted.ProjectId, ct));

        await using (var verify = NewContext())
        {
            await AssertGoneAsync(verify, deleted, ct);
            await AssertIntactAsync(verify, kept, ct);
            Assert.True(await verify.Servers.AnyAsync(item => item.Id == serverId, ct));
        }
        Assert.False(File.Exists(Path.Combine(_storageRoot, deleted.ArtifactPath)));
        Assert.True(File.Exists(Path.Combine(_storageRoot, kept.ArtifactPath)));
        await _gitCli.Received(1).DeleteRepoAsync(RepositoryPath(deleted.ProjectId, "portfolio-repo"), Arg.Any<CancellationToken>());
        await _gitCli.DidNotReceive().DeleteRepoAsync(RepositoryPath(kept.ProjectId, "atlas-repo"), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Deleted", "Project", deleted.ProjectId,
            Arg.Is<string>(details => details.Contains("Pipelines=2") && details.Contains($"TaskLogLines={ManyLogLines}")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_WhileARunIsInProgress_IsRefusedAndRemovesNothing()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        SeededProject project;
        await using (var db = NewContext())
        {
            var (organization, server, user, role) = await SeedSharedAsync(db, ct);
            project = await SeedProjectAsync(db, "Portfolio", organization, server, user, role, 2, 9001, ct);
            db.PipelineRuns.Add(new PipelineRun { PipelineId = project.PipelineId, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        await using (var db = NewContext())
        {
            var preview = await BuildService(db).GetPreviewAsync(project.ProjectId, ct);
            Assert.Equal(1, preview!.ActiveRunCount);
            var refusal = await Assert.ThrowsAsync<ConflictException>(() => BuildService(db).DeleteAsync(project.ProjectId, ct));
            Assert.Contains("in progress", refusal.Message, StringComparison.Ordinal);
        }

        await using (var verify = NewContext())
            await AssertIntactAsync(verify, project, ct);
        Assert.True(File.Exists(Path.Combine(_storageRoot, project.ArtifactPath)));
        await _gitCli.DidNotReceiveWithAnyArgs().DeleteRepoAsync(default!, ct);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, ct);
    }

    [Fact]
    public async Task Delete_OfAMissingProject_ReturnsFalse()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        Assert.False(await BuildService(db).DeleteAsync(424242, TestContext.Current.CancellationToken));
        Assert.Null(await BuildService(db).GetPreviewAsync(424242, TestContext.Current.CancellationToken));
    }

    private ProjectDeletionService BuildService(AppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = _storageRoot })
            .Build();
        var gitLight = Substitute.For<IGitLightService>();
        gitLight.ResolveDiskPath(Arg.Any<int>(), Arg.Any<string>())
            .Returns(call => RepositoryPath(call.ArgAt<int>(0), call.ArgAt<string>(1)));
        return new ProjectDeletionService(
            new ProjectDeletionRepository(db),
            new DbTransactionScope(db),
            new ArtifactStorageService(configuration),
            gitLight,
            _gitCli,
            Substitute.For<IPortRegistryService>(),
            _audit,
            Substitute.For<IEntityChangeNotifier>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ProjectDeletionService>.Instance);
    }

    private string RepositoryPath(int projectId, string slug) => Path.Combine(_storageRoot, "git", $"{projectId}", slug + ".git");

    private static async Task<(Organization, Server, User, Role)> SeedSharedAsync(AppDbContext db, CancellationToken ct)
    {
        var organization = new Organization { Name = "Org deletion", Slug = "orgdeletion", Description = "d" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var server = new Server { Name = "shared-node", Hostname = "shared-node", OrganizationId = organization.Id };
        var user = new User { Username = "deleter", PasswordHash = "x" };
        var role = new Role { Name = "deletion-role", Description = "d" };
        db.AddRange(server, user, role);
        await db.SaveChangesAsync(ct);
        return (organization, server, user, role);
    }

    private async Task<SeededProject> SeedProjectAsync(
        AppDbContext db, string name, Organization organization, Server server, User user, Role role,
        int logLines, int port, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var project = new Project { Name = name, Description = "d", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        var environment = new Environment { Name = name + "-prod", ProjectId = project.Id };
        var projectServer = new ProjectServer { ProjectId = project.Id, ServerId = server.Id, Type = ProjectServerType.AgentServer, DisplayName = name + "-ps", Host = "shared-node" };
        db.AddRange(environment, projectServer);
        await db.SaveChangesAsync(ct);

        var pipeline = new Pipeline { ProjectId = project.Id, EnvironmentId = environment.Id, Name = name + "-ci", YamlDefinition = "name: ci\nstages: []" };
        var environmentPipeline = new Pipeline { EnvironmentId = environment.Id, Name = name + "-env", YamlDefinition = "name: env\nstages: []" };
        db.Pipelines.AddRange(pipeline, environmentPipeline);
        await db.SaveChangesAsync(ct);

        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = now.AddHours(-1), CompletedAt = now };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var step = new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "compile", Status = TaskExecutionStatus.Success };
        db.PipelineStepRuns.Add(step);
        await db.SaveChangesAsync(ct);
        var task = new ServerTask { ServerId = server.Id, Name = "compile", Command = "dotnet build", PipelineRunId = run.Id, PipelineStepRunId = step.Id, Status = TaskExecutionStatus.Success };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        db.TaskLogs.AddRange(Enumerable.Range(0, logLines).Select(index => new TaskLog { TaskId = task.Id, Message = $"line {index}" }));

        var artifactPath = Path.Combine($"{project.Id}", $"{pipeline.Id}", $"{run.Id}", "app.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_storageRoot, artifactPath))!);
        await File.WriteAllTextAsync(Path.Combine(_storageRoot, artifactPath), "zip", ct);
        var artifact = new PipelineArtifact { PipelineRunId = run.Id, PipelineId = pipeline.Id, ProjectId = project.Id, Name = "app", FilePath = artifactPath };
        var approval = new PipelineApproval { PipelineRunId = run.Id, StageName = "deploy", EnvironmentId = environment.Id, Status = ApprovalStatus.Approved, RequestedAt = now };
        var app = new MonitoredApp { ProjectId = project.Id, EnvironmentId = environment.Id, Name = name + "-web" };
        db.AddRange(artifact, approval, app);
        await db.SaveChangesAsync(ct);
        db.AppHealthSamples.AddRange(
            new AppHealthSample { MonitoredAppId = app.Id, Timestamp = now.AddMinutes(-1), IsUp = true },
            new AppHealthSample { MonitoredAppId = app.Id, Timestamp = now, IsUp = true });

        var projectVault = new Vault { Name = name + "-vault", Description = "d", ProjectId = project.Id };
        var environmentVault = new Vault { Name = name + "-env-vault", Description = "d", EnvironmentId = environment.Id };
        var serverVault = new Vault { Name = name + "-ps-vault", Description = "d", ProjectServerId = projectServer.Id };
        var library = new VariableLibrary { Name = name + "-lib", Description = "d", ProjectId = project.Id };
        var connection = new ServiceConnection { Name = name + "-registry", ProjectId = project.Id, EncryptedPayload = "enc" };
        var feed = new PackageFeed { Name = name + "-feed", ProjectId = project.Id, UpstreamUrl = "https://example.test" };
        var policy = new BackupPolicy { Name = name + "-db", ProjectId = project.Id, ServerId = server.Id, ScheduleCron = "0 3 * * *" };
        var repository = new GitInternalRepo { ProjectId = project.Id, Name = name, Slug = name.ToLowerInvariant() + "-repo" };
        var source = new Release { ProjectId = project.Id, Version = "1.0.0", BranchName = "main" };
        var target = new Release { ProjectId = project.Id, Version = "1.1.0", BranchName = "main" };
        var parent = new WorkItem { ProjectId = project.Id, Title = "epic" };
        db.AddRange(projectVault, environmentVault, serverVault, library, connection, feed, policy, repository, source, target, parent);
        db.ProjectSubscriptions.Add(new ProjectSubscription { ProjectId = project.Id, UserId = user.Id });
        db.ServerPortReservations.Add(new ServerPortReservation
        {
            ServerId = server.Id,
            Port = port,
            OwnerKey = $"project:{project.Id}",
            OwnerLabel = name,
            ProjectId = project.Id,
            Source = PortReservationSource.Declared,
        });
        await db.SaveChangesAsync(ct);

        var secret = new VaultSecret { VaultId = serverVault.Id, Key = "k", EncryptedValue = "enc" };
        var child = new WorkItem { ProjectId = project.Id, Title = "story", ParentId = parent.Id };
        var rollback = new ReleaseRollback { SourceReleaseId = target.Id, TargetReleaseId = source.Id, PipelineId = pipeline.Id };
        db.AddRange(secret, child, rollback,
            new PackageEntry { PackageFeedId = feed.Id, Name = "lib" },
            new BackupRun { BackupPolicyId = policy.Id, ServerId = server.Id },
            new ResourcePermission { RoleId = role.Id, ResourceType = ResourceType.Project, ResourceId = project.Id, Permission = Permission.Read },
            new ResourcePermission { RoleId = role.Id, ResourceType = ResourceType.Pipeline, ResourceId = pipeline.Id, Permission = Permission.Read });
        await db.SaveChangesAsync(ct);

        return new SeededProject(
            project.Id, pipeline.Id, environmentPipeline.Id, run.Id, step.Id, task.Id, artifact.Id, artifactPath,
            approval.Id, environment.Id, projectServer.Id, app.Id, [projectVault.Id, environmentVault.Id, serverVault.Id],
            library.Id, connection.Id, feed.Id, policy.Id, repository.Id, [source.Id, target.Id], rollback.Id,
            [parent.Id, child.Id]);
    }

    private static async Task AssertGoneAsync(AppDbContext db, SeededProject project, CancellationToken ct)
    {
        foreach (var (kind, remaining) in await CountOwnedAsync(db, project, ct))
            Assert.True(remaining == 0, $"{kind}: {remaining} row(s) of the deleted project remain.");
    }

    private static async Task AssertIntactAsync(AppDbContext db, SeededProject project, CancellationToken ct)
    {
        foreach (var (kind, remaining) in await CountOwnedAsync(db, project, ct))
            Assert.True(remaining > 0, $"{kind}: the project lost its rows.");
    }

    private static async Task<List<(string Kind, int Count)>> CountOwnedAsync(AppDbContext db, SeededProject p, CancellationToken ct) =>
    [
        ("Project", await db.Projects.CountAsync(item => item.Id == p.ProjectId, ct)),
        ("Pipelines", await db.Pipelines.CountAsync(item => item.Id == p.PipelineId || item.Id == p.EnvironmentPipelineId, ct)),
        ("PipelineRuns", await db.PipelineRuns.CountAsync(item => item.Id == p.RunId, ct)),
        ("PipelineStepRuns", await db.PipelineStepRuns.CountAsync(item => item.Id == p.StepRunId, ct)),
        ("Tasks", await db.Tasks.CountAsync(item => item.Id == p.TaskId, ct)),
        ("TaskLogs", await db.TaskLogs.CountAsync(item => item.TaskId == p.TaskId, ct)),
        ("Artifacts", await db.PipelineArtifacts.CountAsync(item => item.Id == p.ArtifactId, ct)),
        ("Approvals", await db.PipelineApprovals.CountAsync(item => item.Id == p.ApprovalId, ct)),
        ("Environments", await db.Environments.CountAsync(item => item.Id == p.EnvironmentId, ct)),
        ("ProjectServers", await db.ProjectServers.CountAsync(item => item.Id == p.ProjectServerId, ct)),
        ("MonitoredApps", await db.MonitoredApps.CountAsync(item => item.Id == p.MonitoredAppId, ct)),
        ("Telemetry", await db.AppHealthSamples.CountAsync(item => item.MonitoredAppId == p.MonitoredAppId, ct)),
        ("Vaults", await db.Vaults.CountAsync(item => p.VaultIds.Contains(item.Id), ct)),
        ("VaultSecrets", await db.VaultSecrets.CountAsync(item => p.VaultIds.Contains(item.VaultId), ct)),
        ("VariableLibraries", await db.VariableLibraries.CountAsync(item => item.Id == p.LibraryId, ct)),
        ("ServiceConnections", await db.ServiceConnections.CountAsync(item => item.Id == p.ServiceConnectionId, ct)),
        ("PackageFeeds", await db.PackageFeeds.CountAsync(item => item.Id == p.PackageFeedId, ct)),
        ("PackageEntries", await db.PackageEntries.CountAsync(item => item.PackageFeedId == p.PackageFeedId, ct)),
        ("BackupPolicies", await db.BackupPolicies.CountAsync(item => item.Id == p.BackupPolicyId, ct)),
        ("BackupRuns", await db.BackupRuns.CountAsync(item => item.BackupPolicyId == p.BackupPolicyId, ct)),
        ("GitRepositories", await db.GitInternalRepos.CountAsync(item => item.Id == p.GitRepositoryId, ct)),
        ("Releases", await db.Releases.CountAsync(item => p.ReleaseIds.Contains(item.Id), ct)),
        ("ReleaseRollbacks", await db.ReleaseRollbacks.CountAsync(item => item.Id == p.RollbackId, ct)),
        ("WorkItems", await db.WorkItems.CountAsync(item => p.WorkItemIds.Contains(item.Id), ct)),
        ("Subscriptions", await db.ProjectSubscriptions.CountAsync(item => item.ProjectId == p.ProjectId, ct)),
        ("PortReservations", await db.ServerPortReservations.CountAsync(item => item.OwnerKey == $"project:{p.ProjectId}", ct)),
        ("Permissions", await db.ResourcePermissions.CountAsync(item =>
            (item.ResourceType == ResourceType.Project && item.ResourceId == p.ProjectId)
            || (item.ResourceType == ResourceType.Pipeline && item.ResourceId == p.PipelineId), ct)),
    ];

    private sealed record SeededProject(
        int ProjectId, int PipelineId, int EnvironmentPipelineId, int RunId, int StepRunId, int TaskId,
        int ArtifactId, string ArtifactPath, int ApprovalId, int EnvironmentId, int ProjectServerId,
        int MonitoredAppId, int[] VaultIds, int LibraryId, int ServiceConnectionId, int PackageFeedId,
        int BackupPolicyId, int GitRepositoryId, int[] ReleaseIds, int RollbackId, int[] WorkItemIds);
}
