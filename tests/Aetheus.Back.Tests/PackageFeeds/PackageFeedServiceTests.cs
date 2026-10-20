// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.PackageFeeds;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PackageFeedServiceTests
{
    private readonly IPackageFeedRepository _repo = Substitute.For<IPackageFeedRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IPackageVersionResolver _resolver = Substitute.For<IPackageVersionResolver>();
    private readonly PackageFeedSyncGate _gate = new();
    private readonly IAdminChangeNotifier _notifier = Substitute.For<IAdminChangeNotifier>();
    private readonly PackageFeedService _sut;

    public PackageFeedServiceTests()
    {
        _sut = new PackageFeedService(
            _repo, _audit, _resolver, _gate, TimeProvider.System, _notifier);
    }

    [Fact]
    public async Task SyncFeedAsync_NotFound_ReturnsNull()
    {
        _repo.FindFeedAsync(99, Arg.Any<CancellationToken>()).Returns((PackageFeed?)null);
        Assert.Null(await _sut.SyncFeedAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SyncFeedAsync_WhenAlreadyRunning_SkipsWithoutDuplicatingOutboundWork()
    {
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PackageFeed { Id = 1, Name = "nuget", FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://api.nuget.org" });

        // Occupy the per-feed gate as if a concurrent sync (hosted timer vs manual) were already in flight.
        Assert.True(await _gate.TryEnterAsync(1, TestContext.Current.CancellationToken));
        try
        {
            var result = await _sut.SyncFeedAsync(1, ct: TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.Equal(0, result!.Synced);
            Assert.Contains("already in progress", result.Message, StringComparison.OrdinalIgnoreCase);
            // Skipped, not duplicated: the upstream registry must not be probed a second time.
            await _resolver.DidNotReceive().ResolveLatestAsync(
                Arg.Any<PackageFeedType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            _gate.Release(1);
        }
    }

    [Fact]
    public async Task SyncFeedAsync_ResolvesVersions_UpdatesEntriesAndCounts()
    {
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PackageFeed { Id = 1, Name = "nuget", FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://api.nuget.org" });
        var entry = new PackageEntry { Id = 5, PackageFeedId = 1, Name = "Newtonsoft.Json", LatestVersion = "" };
        _repo.GetTrackedPackagesAsync(1, Arg.Any<CancellationToken>()).Returns([entry]);
        _resolver.ResolveLatestAsync(PackageFeedType.NuGet, "https://api.nuget.org", "Newtonsoft.Json", Arg.Any<CancellationToken>())
            .Returns(new PackageResolveResult(PackageResolveOutcome.Resolved, "13.0.3", null));

        var result = await _sut.SyncFeedAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Synced);
        Assert.Equal("13.0.3", entry.LatestVersion);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(
            AdminEntities.PackageFeed, 1, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncFeedAsync_UnsupportedFeedType_ReportsUnsupported_NoFabrication()
    {
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PackageFeed { Id = 1, Name = "mvn", FeedType = PackageFeedType.Maven, UpstreamUrl = "https://repo1.maven.org" });
        var entry = new PackageEntry { Id = 5, PackageFeedId = 1, Name = "com.google.guava:guava", LatestVersion = "" };
        _repo.GetTrackedPackagesAsync(1, Arg.Any<CancellationToken>()).Returns([entry]);
        _resolver.ResolveLatestAsync(PackageFeedType.Maven, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PackageResolveResult(PackageResolveOutcome.Unsupported, null, null));

        var result = await _sut.SyncFeedAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result!.Synced);
        Assert.Equal(1, result.Unsupported);
        Assert.Equal(string.Empty, entry.LatestVersion); // never fabricated
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task SyncFeedAsync_RateLimited_StopsFeedAndDefersNextAttempt()
    {
        const int feedId = 77;
        _repo.FindFeedAsync(feedId, Arg.Any<CancellationToken>())
            .Returns(new PackageFeed
            {
                Id = feedId,
                Name = "npm",
                FeedType = PackageFeedType.Npm,
                UpstreamUrl = "https://registry.npmjs.org"
            });
        _repo.GetTrackedPackagesAsync(feedId, Arg.Any<CancellationToken>()).Returns(
        [
            new PackageEntry { Id = 1, PackageFeedId = feedId, Name = "first", LatestVersion = "" },
            new PackageEntry { Id = 2, PackageFeedId = feedId, Name = "second", LatestVersion = "" }
        ]);
        _resolver.ResolveLatestAsync(
                PackageFeedType.Npm,
                "https://registry.npmjs.org",
                "first",
                Arg.Any<CancellationToken>())
            .Returns(new PackageResolveResult(
                PackageResolveOutcome.RateLimited,
                null,
                null,
                TimeSpan.FromMinutes(2)));

        var first = await _sut.SyncFeedAsync(feedId, ct: TestContext.Current.CancellationToken);
        var second = await _sut.SyncFeedAsync(feedId, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, first!.Failed);
        Assert.Contains("rate limit", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rate limit", second!.Message, StringComparison.OrdinalIgnoreCase);
        await _resolver.Received(1).ResolveLatestAsync(
            Arg.Any<PackageFeedType>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePackageAsync_WrongFeed_ReturnsFalse()
    {
        _repo.FindPackageAsync(5, Arg.Any<CancellationToken>())
            .Returns(new PackageEntry { Id = 5, PackageFeedId = 999, Name = "x", LatestVersion = "" });

        Assert.False(await _sut.RemovePackageAsync(1, 5, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetFeedsAsync_ReturnsRepositoryProjection()
    {
        // The repository now projects to PackageFeedDto directly (package COUNT, no entity load);
        // the service passes it through.
        _repo.GetPagedFeedsAsync(null, null, 1, 25, null, false, Arg.Any<CancellationToken>())
            .Returns(([new PackageFeedDto { Id = 1, Name = "nuget-feed", FeedType = PackageFeedType.NuGet, PackageCount = 3 }], 1));

        var result = await _sut.GetFeedsAsync(null, new PaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("nuget-feed", result.Items[0].Name);
        Assert.Equal(3, result.Items[0].PackageCount);
    }

    [Fact]
    public async Task GetFeedsAsync_WithProjectFilter_PassesProjectId()
    {
        _repo.GetPagedFeedsAsync(5, "pkg", 2, 10, "PackageCount", true, Arg.Any<CancellationToken>())
            .Returns((new List<PackageFeedDto>(), 0));

        var result = await _sut.GetFeedsAsync(5, new PaginationRequest
        {
            Page = 2,
            PageSize = 10,
            Search = "pkg",
            SortBy = "PackageCount",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        await _repo.Received(1).GetPagedFeedsAsync(
            5, "pkg", 2, 10, "PackageCount", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFeedDetailAsync_Found_ReturnsDtoWithPackages()
    {
        _repo.GetFeedDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PackageFeed
            {
                Id = 1,
                Name = "npm-feed",
                FeedType = PackageFeedType.Npm,
                Packages = [new PackageEntry { Id = 1, Name = "lodash", LatestVersion = "4.17.21" }]
            });

        var result = await _sut.GetFeedDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("npm-feed", result.Name);
        Assert.Single(result.Packages);
        Assert.Equal("lodash", result.Packages[0].Name);
    }

    [Fact]
    public async Task GetFeedDetailAsync_NotFound_ReturnsNull()
    {
        _repo.GetFeedDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((PackageFeed?)null);

        Assert.Null(await _sut.GetFeedDetailAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateFeedAsync_CreatesAndReturnsDto()
    {
        _repo.AddFeedAsync(Arg.Any<PackageFeed>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateFeedAsync(new CreatePackageFeedRequest
        {
            Name = "docker-feed",
            FeedType = PackageFeedType.Docker,
            UpstreamUrl = "https://registry.docker.io"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("docker-feed", result.Name);
        await _repo.Received(1).AddFeedAsync(Arg.Any<PackageFeed>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Created", "PackageFeed", Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(
            AdminEntities.PackageFeed, Arg.Any<int>(), EntityChangeOps.Created, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateFeedAsync_NotFound_ReturnsNull()
    {
        _repo.FindFeedAsync(99, Arg.Any<CancellationToken>())
            .Returns((PackageFeed?)null);

        Assert.Null(await _sut.UpdateFeedAsync(99, new UpdatePackageFeedRequest { Name = "x", UpstreamUrl = "https://a.com" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateFeedAsync_Found_UpdatesAndReturns()
    {
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PackageFeed { Id = 1, Name = "old", FeedType = PackageFeedType.NuGet, Packages = [] });
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateFeedAsync(1, new UpdatePackageFeedRequest
        {
            Name = "updated",
            UpstreamUrl = "https://nuget.org"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("updated", result.Name);
        await _audit.Received(1).LogAsync("Updated", "PackageFeed", 1, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteFeedAsync_NotFound_ReturnsFalse()
    {
        _repo.FindFeedAsync(99, Arg.Any<CancellationToken>())
            .Returns((PackageFeed?)null);

        Assert.False(await _sut.DeleteFeedAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteFeedAsync_Found_DeletesAndReturnsTrue()
    {
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PackageFeed { Id = 1, Name = "feed" });
        _repo.RemoveFeedAsync(Arg.Any<PackageFeed>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Assert.True(await _sut.DeleteFeedAsync(1, ct: TestContext.Current.CancellationToken));
        await _repo.Received(1).RemoveFeedAsync(Arg.Any<PackageFeed>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Deleted", "PackageFeed", 1, null, Arg.Any<CancellationToken>());
    }

    // --- AddPackageAsync ---

    private static PackageFeed NuGetFeed() =>
        new() { Id = 1, Name = "nuget", FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://api.nuget.org" };

    [Fact]
    public async Task AddPackageAsync_UnknownFeed_ReturnsNullWithoutStoringAnything()
    {
        Assert.Null(await _sut.AddPackageAsync(9, new AddPackageRequest { Name = "x" }, TestContext.Current.CancellationToken));
        await _repo.DidNotReceiveWithAnyArgs().AddPackageAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddPackageAsync_AlreadyTracked_ReturnsTheExistingEntryWithoutResolving()
    {
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>()).Returns(NuGetFeed());
        _repo.FindPackageByNameAsync(1, "Serilog", Arg.Any<CancellationToken>())
            .Returns(new PackageEntry { Id = 3, PackageFeedId = 1, Name = "Serilog", LatestVersion = "4.0.0" });

        var dto = await _sut.AddPackageAsync(1, new AddPackageRequest { Name = "Serilog" }, TestContext.Current.CancellationToken);

        Assert.Equal((3, "4.0.0"), (dto!.Id, dto.LatestVersion));
        await _repo.DidNotReceiveWithAnyArgs().AddPackageAsync(default!, TestContext.Current.CancellationToken);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveLatestAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddPackageAsync_Resolved_StoresTheUpstreamVersionAndPublicationDate()
    {
        var published = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>()).Returns(NuGetFeed());
        _resolver.ResolveLatestAsync(PackageFeedType.NuGet, "https://api.nuget.org", "Serilog", Arg.Any<CancellationToken>())
            .Returns(new PackageResolveResult(PackageResolveOutcome.Resolved, "4.1.0", published));

        var dto = await _sut.AddPackageAsync(1, new AddPackageRequest { Name = "Serilog" }, TestContext.Current.CancellationToken);

        Assert.Equal(("Serilog", "4.1.0", published), (dto!.Name, dto.LatestVersion, dto.PublishedAt));
        Assert.NotEqual(default, dto.LastSyncedAt);
        await _repo.Received(1).AddPackageAsync(Arg.Is<PackageEntry>(e => e.PackageFeedId == 1), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Created", "PackageEntry", Arg.Any<int>(), "nuget/Serilog", Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(AdminEntities.PackageFeed, 1, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddPackageAsync_NotResolved_KeepsAnHonestBlankVersion()
    {
        _repo.FindFeedAsync(1, Arg.Any<CancellationToken>()).Returns(NuGetFeed());
        _resolver.ResolveLatestAsync(PackageFeedType.NuGet, Arg.Any<string>(), "ghost", Arg.Any<CancellationToken>())
            .Returns(new PackageResolveResult(PackageResolveOutcome.NotFound, null, null));

        var dto = await _sut.AddPackageAsync(1, new AddPackageRequest { Name = "ghost" }, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, dto!.LatestVersion);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Created", "PackageEntry", Arg.Any<int>(), "nuget/ghost", Arg.Any<CancellationToken>());
    }
}
