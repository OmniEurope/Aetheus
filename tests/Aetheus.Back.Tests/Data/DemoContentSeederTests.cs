// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.DataSeed;

public sealed class DemoContentSeederTests
{
    [Fact]
    public async Task SeedAsync_MaterializesGitBranchesAndArtifacts_ThenIsIdempotent()
    {
        await using var db = CreateContext();
        db.Organizations.Add(new Organization { Name = "Aetheus", Slug = "aetheus" });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));
        var seed = await DemoDataSeeder.SeedDemoAsync(db, clock);
        Assert.NotNull(seed);

        var trackedRepo = new GitInternalRepo
        {
            ProjectId = seed.TotoProjectId,
            Name = "toto",
            Slug = "toto",
            DefaultBranch = "main",
            IsEmpty = true
        };
        db.GitInternalRepos.Add(trackedRepo);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var repo = new GitLightRepoDto
        {
            Id = trackedRepo.Id,
            ProjectId = seed.TotoProjectId,
            Name = "toto",
            Slug = "toto",
            DefaultBranch = "main",
            IsEmpty = true
        };

        var git = Substitute.For<IGitLightService>();
        git.GetRepositoriesAsync(seed.TotoProjectId, Arg.Any<CancellationToken>()).Returns([repo]);
        git.GetBranchesAsync(repo.Id, Arg.Any<CancellationToken>()).Returns([]);
        var pipelineGit = Substitute.For<IPipelineGitService>();
        pipelineGit.WriteProjectPipelineYamlAsync(
                seed.TotoProjectId, Arg.Any<string>(), Arg.Any<string>(), "demo-seed",
                Arg.Any<CancellationToken>(), "main", repo.Id)
            .Returns((GitWriteOutcome.Committed, (string?)null));
        var storage = Substitute.For<IArtifactStorageService>();
        storage.SaveArtifactAsync(
                seed.TotoProjectId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => ($"demo/{call.ArgAt<string>(3)}", "abc123"));
        var sut = new DemoContentSeeder(
            db, git, pipelineGit, storage, clock, Substitute.For<ILogger<DemoContentSeeder>>());

        await sut.SeedAsync(seed, ct: TestContext.Current.CancellationToken);

        Assert.False((await db.GitInternalRepos.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).IsEmpty);
        Assert.Equal(2, await db.PipelineArtifacts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        await git.Received(2).CreateBranchAsync(repo.Id, Arg.Any<CreateGitLightBranchRequest>(), Arg.Any<CancellationToken>());
        await git.Received(1).EnsureRepositoryInitializedAsync(repo.Id, Arg.Any<CancellationToken>());
        await pipelineGit.Received(DemoDataSeeder.TotoPipelineDefinitions().Count)
            .WriteProjectPipelineYamlAsync(seed.TotoProjectId, Arg.Any<string>(), Arg.Any<string>(),
                "demo-seed", Arg.Any<CancellationToken>(), "main", repo.Id);

        pipelineGit.ReadProjectPipelineYamlAsync(
                seed.TotoProjectId, Arg.Any<string>(), Arg.Any<CancellationToken>(), "main", repo.Id)
            .Returns(call => DemoDataSeeder.TotoPipelineDefinitions()[call.ArgAt<string>(1)]);
        git.GetBranchesAsync(repo.Id, Arg.Any<CancellationToken>()).Returns([
            new GitLightBranchDto { Name = "qa" }, new GitLightBranchDto { Name = "release/demo" }
        ]);
        storage.OpenArtifact(Arg.Any<string>()).Returns(_ => new MemoryStream([1]));

        await sut.SeedAsync(seed, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.PipelineArtifacts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        await git.Received(2).EnsureRepositoryInitializedAsync(repo.Id, Arg.Any<CancellationToken>());
        await pipelineGit.Received(DemoDataSeeder.TotoPipelineDefinitions().Count)
            .WriteProjectPipelineYamlAsync(seed.TotoProjectId, Arg.Any<string>(), Arg.Any<string>(),
                "demo-seed", Arg.Any<CancellationToken>(), "main", repo.Id);
    }

    [Fact]
    public async Task StartupSeeder_ProductionWithoutQaTier_DoesNothing()
    {
        await using var db = CreateContext();
        var services = Substitute.For<IServiceProvider>();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:Demo"] = "true",
            ["Aetheus:EnvironmentTier"] = "prod"
        }).Build();

        await DemoDataStartupSeeder.SeedIfEnabledAsync(services, db, environment, configuration);

        Assert.Empty(await db.Projects.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        services.DidNotReceiveWithAnyArgs().GetService(default!);
    }

    [Fact]
    public async Task SeedAsync_NullSeed_FailsFast()
    {
        await using var db = CreateContext();
        var sut = new DemoContentSeeder(
            db, Substitute.For<IGitLightService>(), Substitute.For<IPipelineGitService>(),
            Substitute.For<IArtifactStorageService>(), TimeProvider.System,
            Substitute.For<ILogger<DemoContentSeeder>>());

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.SeedAsync(null!, ct: TestContext.Current.CancellationToken));
    }

    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
