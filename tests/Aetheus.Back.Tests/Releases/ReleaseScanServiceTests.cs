// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Releases;

public class ReleaseScanServiceTests
{
    private static (ReleaseScanService Sut, IProjectRepository ProjectRepo, IReleaseService ReleaseService, IClientProxy Proxy) BuildSut()
    {
        var projectRepo = Substitute.For<IProjectRepository>();
        var releaseService = Substitute.For<IReleaseService>();
        var hub = Substitute.For<IHubContext<ReleaseHub>>();
        var proxy = Substitute.For<IClientProxy>();
        hub.Clients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(proxy);

        var sp = new ServiceCollection()
            .AddScoped(_ => projectRepo)
            .AddScoped(_ => releaseService)
            .AddScoped(_ => hub)
            .BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var config = new ConfigurationBuilder().Build();
        var logger = Substitute.For<ILogger<ReleaseScanService>>();
        return (new ReleaseScanService(scopeFactory, config, logger), projectRepo, releaseService, proxy);
    }

    [Fact]
    public async Task ScanPendingReleasesAsync_SyncsEachProjectAndBroadcastsWhenReleasesFound()
    {
        var (sut, projectRepo, releaseService, proxy) = BuildSut();

        var project = new Project { Id = 42, Name = "alpha" };
        projectRepo.GetAllProjectsWithRepoUrlAsync(Arg.Any<CancellationToken>()).Returns([project]);
        releaseService.SyncReleasesAsync(42, Arg.Any<CancellationToken>())
            .Returns([new ReleaseDto { Id = 1, ProjectId = 42, Version = "1.0.0" }]);

        await sut.ScanPendingReleasesAsync(TestContext.Current.CancellationToken);

        await releaseService.Received(1).SyncReleasesAsync(42, Arg.Any<CancellationToken>());
        // SendAsync(...) is an extension over SendCoreAsync - verify the broadcast happened.
        await proxy.Received(1).SendCoreAsync("ReleasesUpdated", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanPendingReleasesAsync_NoReleases_DoesNotBroadcast()
    {
        var (sut, projectRepo, releaseService, proxy) = BuildSut();

        var project = new Project { Id = 7, Name = "beta" };
        projectRepo.GetAllProjectsWithRepoUrlAsync(Arg.Any<CancellationToken>()).Returns([project]);
        releaseService.SyncReleasesAsync(7, Arg.Any<CancellationToken>()).Returns([]);

        await sut.ScanPendingReleasesAsync(TestContext.Current.CancellationToken);

        await releaseService.Received(1).SyncReleasesAsync(7, Arg.Any<CancellationToken>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }
}
