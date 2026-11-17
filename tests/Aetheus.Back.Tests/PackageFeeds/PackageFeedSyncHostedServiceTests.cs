// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageFeeds;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageFeeds;

public class PackageFeedSyncHostedServiceTests
{
    [Fact]
    public async Task SyncAllAsync_SyncsEveryFeed()
    {
        var repo = Substitute.For<IPackageFeedRepository>();
        var feedService = Substitute.For<IPackageFeedService>();
        repo.GetFeedIdsAsync(Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
        feedService.SyncFeedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PackageFeedSyncResultDto { Synced = 1 });

        var provider = new ServiceCollection()
            .AddScoped(_ => repo)
            .AddScoped(_ => feedService)
            .BuildServiceProvider();

        var sut = new PackageFeedSyncHostedService(provider, new ConfigurationBuilder().Build(),
            NullLogger<PackageFeedSyncHostedService>.Instance,
            leaderLease: LeaderLeaseDouble.Leader());

        await sut.SyncAllAsync(TestContext.Current.CancellationToken);

        await feedService.Received(1).SyncFeedAsync(1, Arg.Any<CancellationToken>());
        await feedService.Received(1).SyncFeedAsync(2, Arg.Any<CancellationToken>());
        await feedService.Received(1).SyncFeedAsync(3, Arg.Any<CancellationToken>());
    }
}
