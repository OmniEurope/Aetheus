// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// The project detail loader follows the releases hub of the loaded project on a real hub connection to
/// an in-memory hub: it joins the project's group, reloads the releases on a created or changed release and
/// on an update of its own project (not another's), keeps them when they cannot be read, drops a
/// connection the hub refuses, moves to the next project's group, and rejoins its group and reloads the
/// releases after a reconnection.
/// </summary>
public sealed class ProjectDetailLoaderReleaseHubTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly InMemoryHubConnectionFactory _hubs;

    public ProjectDetailLoaderReleaseHubTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _hubs = new InMemoryHubConnectionFactory(Services.GetRequiredService<AuthStateProvider>());
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects/1", new ProjectDetailDto { Id = 1, Name = "P1" });
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects/2", new ProjectDetailDto { Id = 2, Name = "P2" });
        ServeReleases(1);
    }

    private InMemoryHubServer Hub => _hubs.Server("releases");

    private void ServeReleases(int count) =>
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [.. Enumerable.Range(1, count).Select(id => new ReleaseDto { Id = id, Version = $"1.{id}.0" })],
            TotalCount = count
        });

    private async Task<ProjectDetailLoader> LoadAsync(int projectId = 1)
    {
        var loader = new ProjectDetailLoader(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<NavigationManager>(),
            NullLogger<ProjectDetailLoader>.Instance,
            _hubs);
        await loader.EnsureLoadedAsync(projectId, Xunit.TestContext.Current.CancellationToken);
        await Hub.WhenInvokedAsync("JoinProjectGroup");
        return loader;
    }

    [Fact]
    public async Task ACreatedOrChangedRelease_ReloadsTheReleases()
    {
        await using var loader = await LoadAsync();
        Assert.Equal([1], Hub.IntArguments("JoinProjectGroup"));
        var changes = 0;
        loader.OnChanged += () => Interlocked.Increment(ref changes);

        ServeReleases(2);
        await Hub.PushAsync("ReleaseCreated", new ReleaseDto { Id = 2, Version = "1.2.0" });
        Assert.Equal(2, loader.Releases.Count);

        ServeReleases(3);
        await Hub.PushAsync("ReleaseStatusChanged", new ReleaseDto { Id = 2, Version = "1.2.0" });
        Assert.Equal(3, loader.Releases.Count);
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task AnUpdateOfAnotherProject_ReloadsNothing_OneOfItsOwnDoes()
    {
        await using var loader = await LoadAsync();
        ServeReleases(4);

        await Hub.PushAsync("ReleasesUpdated", 2, new List<ReleaseDto>());
        Assert.Single(loader.Releases);

        await Hub.PushAsync("ReleasesUpdated", 1, new List<ReleaseDto>());
        Assert.Equal(4, loader.Releases.Count);
    }

    [Fact]
    public async Task ReleasesThatCannotBeRead_AreKept()
    {
        await using var loader = await LoadAsync();
        _handler.SetResponse(HttpMethod.Get, "api/releases", System.Net.HttpStatusCode.Unauthorized);
        var reads = ReleaseReads();

        await Hub.PushAsync("ReleaseCreated", new ReleaseDto { Id = 2 });
        Assert.True(ReleaseReads() > reads);

        Assert.Equal("1.1.0", Assert.Single(loader.Releases).Version);
    }

    [Fact]
    public async Task ARefusedJoin_LeavesTheLoaderWithoutLiveReleases()
    {
        Hub.Refuse("JoinProjectGroup");
        await using var loader = await LoadAsync();

        Assert.NotNull(loader.Project);
        await Hub.WhenAsync(() => Hub.Connections == 1, "the releases hub connection");
        Assert.Single(loader.Releases);
    }

    [Fact]
    public async Task TheNextProject_JoinsItsOwnGroup_AndTheFirstProjectsUpdatesAreIgnored()
    {
        await using var loader = await LoadAsync();

        await loader.EnsureLoadedAsync(2, Xunit.TestContext.Current.CancellationToken);
        await Hub.WhenInvokedAsync("JoinProjectGroup", 2);
        ServeReleases(5);
        await Hub.PushAsync("ReleasesUpdated", 1, new List<ReleaseDto>());

        Assert.Equal([1, 2], Hub.IntArguments("JoinProjectGroup"));
        Assert.Equal(2, loader.Project!.Id);
        Assert.Single(loader.Releases);
    }

    [Fact]
    public async Task AfterAReconnect_TheProjectGroupIsJoinedAgain_AndTheReleasesReloaded()
    {
        await using var loader = await LoadAsync();
        var reads = ReleaseReads();

        await Hub.DropAsync();

        await Hub.WhenInvokedAsync("JoinProjectGroup", 2);
        await _handler.WhenRequestedAsync(request => request.Url.Contains("api/releases", StringComparison.Ordinal), reads + 1, "the releases read again");
        Assert.Equal([1, 1], Hub.IntArguments("JoinProjectGroup"));
    }

    private int ReleaseReads() =>
        _handler.RequestsSnapshot().Count(request => request.Url.Contains("api/releases", StringComparison.Ordinal));
}
