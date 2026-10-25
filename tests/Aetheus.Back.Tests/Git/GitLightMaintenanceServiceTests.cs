// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Git;

public class GitLightMaintenanceServiceTests
{
    private static (GitLightMaintenanceService Sut, IGitLightRepository LightRepo, IGitLightCliService Cli, IGitLightService LightService) BuildSut()
    {
        var lightRepo = Substitute.For<IGitLightRepository>();
        var cli = Substitute.For<IGitLightCliService>();
        var lightService = Substitute.For<IGitLightService>();
        var sp = new ServiceCollection()
            .AddScoped(_ => lightRepo)
            .AddScoped(_ => cli)
            .AddScoped(_ => lightService)
            .BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new GitLightOptions { MaintenanceIntervalHours = 1 });
        var logger = Substitute.For<ILogger<GitLightMaintenanceService>>();
        return (new GitLightMaintenanceService(scopeFactory, options, logger), lightRepo, cli, lightService);
    }

    [Fact]
    public async Task RunMaintenanceAsync_RunsGcOnExistingRepoDirectories()
    {
        var (sut, lightRepo, cli, lightService) = BuildSut();

        // Real temp directory so the Directory.Exists guard passes deterministically.
        var diskPath = Path.Combine(Path.GetTempPath(), "aetheus-gitgc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diskPath);
        try
        {
            var repo = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "repo-1" };
            lightRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([repo]);
            lightService.ResolveDiskPath(1, "repo-1").Returns(diskPath);

            await sut.RunMaintenanceAsync(TestContext.Current.CancellationToken);

            await cli.Received(1).RunGcAsync(diskPath, Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(diskPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunMaintenanceAsync_SkipsGcWhenRepoDirectoryMissing()
    {
        var (sut, lightRepo, cli, lightService) = BuildSut();

        var missingPath = Path.Combine(Path.GetTempPath(), "aetheus-gitgc-missing-" + Guid.NewGuid().ToString("N"));
        var repo = new GitInternalRepo { Id = 2, ProjectId = 1, Slug = "repo-2" };
        lightRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([repo]);
        lightService.ResolveDiskPath(1, "repo-2").Returns(missingPath);

        await sut.RunMaintenanceAsync(TestContext.Current.CancellationToken);

        await cli.DidNotReceive().RunGcAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunMaintenanceAsync_ReconcilesDefaultBranchOutsideListRequest()
    {
        var (sut, lightRepo, cli, lightService) = BuildSut();
        var diskPath = Path.Combine(Path.GetTempPath(), "aetheus-gitbranch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diskPath);
        try
        {
            var repo = new GitInternalRepo
            {
                Id = 3,
                ProjectId = 2,
                Slug = "repo-3",
                DefaultBranch = "main"
            };
            lightRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([repo]);
            lightService.ResolveDiskPath(2, "repo-3").Returns(diskPath);
            cli.DetectDefaultBranchAsync(diskPath, Arg.Any<CancellationToken>())
                .Returns("develop");

            await sut.RunMaintenanceAsync(TestContext.Current.CancellationToken);

            Assert.Equal("develop", repo.DefaultBranch);
            await lightRepo.Received(1).SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(diskPath, recursive: true);
        }
    }
}
