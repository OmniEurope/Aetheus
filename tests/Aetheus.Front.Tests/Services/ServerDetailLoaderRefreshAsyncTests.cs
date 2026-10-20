// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Services;

/// <summary>After an action the loaded server is read again and its new metric points appended; a
/// refresh for a server that is not the loaded one asks nothing.</summary>
public sealed class ServerDetailLoaderRefreshAsyncTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDetailLoaderRefreshAsyncTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/monitoring/servers/", new List<ServerMetricDto>());
    }

    private ServerDetailLoader CreateLoader() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        Services.GetRequiredService<NavigationManager>(),
        NullLogger<ServerDetailLoader>.Instance);

    private static ServerDetailDto Server(string name) => new()
    {
        Id = 5,
        Name = name,
        Hostname = "10.0.0.5",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
    };

    private int ServerReads() => _handler.Requests.Count(r => r.Url.EndsWith("api/servers/5", StringComparison.Ordinal));

    [Fact]
    public async Task NothingLoaded_AsksNothing()
    {
        var loader = CreateLoader();

        await loader.RefreshAsync(5, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(0, ServerReads());
        Assert.Null(loader.Server);
    }

    [Fact]
    public async Task AnotherServer_AsksNothing()
    {
        _handler.SetJsonResponse("api/servers/5", Server("before"));
        var loader = CreateLoader();
        await loader.EnsureLoadedAsync(5, Xunit.TestContext.Current.CancellationToken);

        await loader.RefreshAsync(6, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(1, ServerReads());
    }

    [Fact]
    public async Task TheLoadedServer_IsReadAgainWithItsNewMetrics()
    {
        _handler.SetJsonResponse("api/servers/5", Server("before"));
        var loader = CreateLoader();
        await loader.EnsureLoadedAsync(5, Xunit.TestContext.Current.CancellationToken);
        var changes = 0;
        loader.OnChanged += () => changes++;
        _handler.SetJsonResponse("api/servers/5", Server("after"));
        _handler.SetJsonResponse(HttpMethod.Get, "api/monitoring/servers/",
            new List<ServerMetricDto> { new() { ServerId = 5, CpuPercent = 42, Timestamp = DateTime.UtcNow } });

        await loader.RefreshAsync(5, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(2, ServerReads());
        Assert.Equal("after", loader.Server?.Name);
        Assert.Equal(42, Assert.Single(loader.Metrics).CpuPercent);
        Assert.Equal(1, changes);
        Assert.NotNull(loader.LastUpdated);
    }
}
