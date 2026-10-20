// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// Coverage for <see cref="ServerDetailLoader.EnsureLoadedAsync"/> - specifically the 23
/// lines that are currently zero-covered. Tests cover:
/// - initial load (Server populated, InitialLoadCompleted = true)
/// - server with MemoryTotalMb > 0 sets MetricsReceived / LastUpdated
/// - no-op path (same id, server already loaded)
/// - reload path (different id tears down and reloads)
/// - 404 response leaves Server null but sets InitialLoadCompleted
/// - OnChanged fires after load
/// </summary>
public class ServerDetailLoaderEnsureLoadedTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags PrivInst = BindingFlags.NonPublic | BindingFlags.Instance;

    public ServerDetailLoaderEnsureLoadedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/monitoring/servers/",
            new List<ServerMetricDto>());
    }

    private ServerDetailLoader CreateLoader() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        Services.GetRequiredService<NavigationManager>(),
        NullLogger<ServerDetailLoader>.Instance);

    private static void SetCurrentId(ServerDetailLoader loader, int? id) =>
        typeof(ServerDetailLoader).GetField("_currentId", PrivInst)!.SetValue(loader, id);

    private static int? GetCurrentId(ServerDetailLoader loader) =>
        (int?)typeof(ServerDetailLoader).GetField("_currentId", PrivInst)!.GetValue(loader);

    private static ServerDetailDto MakeServer(int id, bool withMemory = false) => new()
    {
        Id = id,
        Name = $"srv-{id}",
        Hostname = "10.0.0.1",
        IpAddress = "10.0.0.1",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        CpuPercent = 10,
        MemoryUsedMb = withMemory ? 4096 : 0,
        MemoryTotalMb = withMemory ? 8192 : 0,
        DiskUsedGb = 10,
        DiskTotalGb = 100,
        AgentVersion = "3.0.0",
        Tags = ["test"],
        Services = [],
        Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
        Apache = new ApacheDataDto(),
        Certbot = new CertbotDataDto(),
        Cron = new CronDataDto(),
        Mail = new MailDataDto(),
        Teamspeak = new TeamspeakDataDto(),
        Portsentry = new PortsentryDataDto(),
        Rkhunter = new RkhunterDataDto()
    };

    // Leaving and returning before asynchronous disposal completes.

    [Fact]
    public async Task ALeaveTeardownThatRunsAfterTheReaderCameBack_KeepsTheLoadedServer()
    {
        _handler.SetJsonResponse("api/servers/5", MakeServer(5));
        var nav = Services.GetRequiredService<NavigationManager>();
        var loader = CreateLoader();
        nav.NavigateTo("/servers/5/tasks");
        await loader.EnsureLoadedAsync(5, Xunit.TestContext.Current.CancellationToken);

        // The reader opens a task, then comes back with the browser's back button.
        nav.NavigateTo("/tasks/22358");
        nav.NavigateTo("/servers/5/tasks");
        await loader.EnsureLoadedAsync(5, Xunit.TestContext.Current.CancellationToken);
        // The teardown the leave asked for only gets its turn now.
        await loader.TeardownOnLeaveAsync();

        Assert.NotNull(loader.Server);
        Assert.True(loader.InitialLoadCompleted);
        Assert.Equal(5, GetCurrentId(loader));
    }

    [Fact]
    public async Task LeavingTheServerPages_EmptiesTheLoader()
    {
        _handler.SetJsonResponse("api/servers/5", MakeServer(5));
        var nav = Services.GetRequiredService<NavigationManager>();
        var loader = CreateLoader();
        nav.NavigateTo("/servers/5/tasks");
        await loader.EnsureLoadedAsync(5, Xunit.TestContext.Current.CancellationToken);

        nav.NavigateTo("/tasks/22358");
        await loader.TeardownOnLeaveAsync();

        Assert.Null(loader.Server);
        Assert.Null(GetCurrentId(loader));
    }

    // ── Initial load: Server populated ───────────────────────────────────────
    // The deterministic test hub fails immediately; the loader must keep the successful
    // HTTP state and complete normally because real-time updates are best effort.

    [Fact]
    public async Task EnsureLoadedAsync_InitialLoad_PopulatesServer()
    {
        var server = MakeServer(1);
        _handler.SetJsonResponse("api/servers/1", server);

        var sut = CreateLoader();
        await sut.EnsureLoadedAsync(1, Xunit.TestContext.Current.CancellationToken);

        Assert.NotNull(sut.Server);
        Assert.Equal(1, sut.Server!.Id);
        Assert.True(sut.InitialLoadCompleted);
    }

    [Fact]
    public async Task EnsureLoadedAsync_DoesNotWaitForRealtimeNegotiation()
    {
        var server = MakeServer(11);
        _handler.SetJsonResponse("api/servers/11", server);
        var blockingHubFactory = new BunitTestHelper.BlockingHubConnectionFactory(
            Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            Services.GetRequiredService<AuthStateProvider>());
        var sut = new ServerDetailLoader(
            Services.GetRequiredService<ApiClient>(),
            blockingHubFactory,
            Services.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance);

        await sut.EnsureLoadedAsync(11, Xunit.TestContext.Current.CancellationToken)
            .WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(11, sut.Server?.Id);
        Assert.True(sut.InitialLoadCompleted);
        await sut.DisposeAsync();
    }

    // ── Server with memory sets MetricsReceived and LastUpdated ──────────────

    [Fact]
    public async Task EnsureLoadedAsync_ServerWithMemory_SetsMetricsReceived()
    {
        var server = MakeServer(2, withMemory: true);
        _handler.SetJsonResponse("api/servers/2", server);

        var sut = CreateLoader();
        await sut.EnsureLoadedAsync(2, Xunit.TestContext.Current.CancellationToken);

        Assert.True(sut.MetricsReceived);
        Assert.NotNull(sut.LastUpdated);
    }

    // ── Server without memory leaves MetricsReceived false ───────────────────

    [Fact]
    public async Task EnsureLoadedAsync_ServerWithoutMemory_MetricsReceivedFalse()
    {
        var server = MakeServer(3, withMemory: false);
        _handler.SetJsonResponse("api/servers/3", server);

        var sut = CreateLoader();
        await sut.EnsureLoadedAsync(3, Xunit.TestContext.Current.CancellationToken);

        Assert.True(sut.InitialLoadCompleted);
        Assert.False(sut.MetricsReceived);
    }

    // ── No-op: same id + server already loaded ────────────────────────────────

    [Fact]
    public async Task EnsureLoadedAsync_SameIdAlreadyLoaded_NoOp()
    {
        var sut = CreateLoader();

        // Pre-load via the guard path (set _currentId + Server manually)
        var server = MakeServer(4);
        typeof(ServerDetailLoader).GetProperty("Server")!.SetValue(sut, server);
        SetCurrentId(sut, 4);

        var changedCount = 0;
        sut.OnChanged += () => changedCount++;

        // Call EnsureLoadedAsync with the same id - should no-op immediately
        await sut.EnsureLoadedAsync(4, Xunit.TestContext.Current.CancellationToken);

        // OnChanged not fired (no work done), server still the same
        Assert.Equal(0, changedCount);
        Assert.NotNull(sut.Server);
    }

    // ── Reload: different id tears down and reloads ───────────────────────────

    [Fact]
    public async Task EnsureLoadedAsync_DifferentId_TeardownAndReload()
    {
        var server5 = MakeServer(5);
        var server6 = MakeServer(6);
        _handler.SetJsonResponse("api/servers/5", server5);
        _handler.SetJsonResponse("api/servers/6", server6);

        var sut = CreateLoader();

        await sut.EnsureLoadedAsync(5, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(5, sut.Server!.Id);

        // Now load server 6 - teardown happens even when hub was never started
        await sut.EnsureLoadedAsync(6, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(6, sut.Server!.Id);
        Assert.Equal(6, GetCurrentId(sut));
    }

    // ── 404 response: the load completes without a server ─────────────────────
    // Propagating the HttpRequestException reached the error boundary ("an error occurred") on
    // /servers/{unknown id}; the layout's "server not found" state needs a completed empty load.

    [Fact]
    public async Task EnsureLoadedAsync_NotFoundServer_CompletesWithoutServer()
    {
        _handler.SetResponse("api/servers/7", System.Net.HttpStatusCode.NotFound);
        var sut = CreateLoader();
        var changed = 0;
        sut.OnChanged += () => changed++;

        await sut.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);

        Assert.Null(sut.Server);
        Assert.True(sut.InitialLoadCompleted);
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task EnsureLoadedAsync_NotFoundServer_IsNotRequestedAgainOnTheNextRender()
    {
        _handler.SetResponse("api/servers/7", System.Net.HttpStatusCode.NotFound);
        var sut = CreateLoader();

        await sut.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        await sut.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);

        Assert.Single(_handler.Requests, request => request.Url.EndsWith("api/servers/7", StringComparison.Ordinal));
    }

    // ── OnChanged fires after successful load ─────────────────────────────────

    [Fact]
    public async Task EnsureLoadedAsync_OnChangedFired_AfterLoad()
    {
        var server = MakeServer(8);
        _handler.SetJsonResponse("api/servers/8", server);

        var sut = CreateLoader();
        var changedCount = 0;
        sut.OnChanged += () => changedCount++;

        await sut.EnsureLoadedAsync(8, Xunit.TestContext.Current.CancellationToken);

        // OnChanged should have fired at least once (after load; hub start may fail in test env)
        Assert.True(changedCount >= 1);
    }

    // ── CancellationToken: cancelled mid-flight does not crash ────────────────

    [Fact]
    public async Task EnsureLoadedAsync_AlreadyCancelledToken_DoesNotHang()
    {
        var server = MakeServer(9);
        _handler.SetJsonResponse("api/servers/9", server);

        var sut = CreateLoader();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.EnsureLoadedAsync(9, cts.Token)
                .WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, Xunit.TestContext.Current.CancellationToken));
        Assert.Null(sut.Server);
        Assert.False(sut.InitialLoadCompleted);
    }

    // ── DisposeAsync while load in-flight ─────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_AfterLoad_ClearsAllState()
    {
        var server = MakeServer(10, withMemory: true);
        _handler.SetJsonResponse("api/servers/10", server);

        var sut = CreateLoader();
        await sut.EnsureLoadedAsync(10, Xunit.TestContext.Current.CancellationToken);

        Assert.NotNull(sut.Server);

        await sut.DisposeAsync();

        Assert.Null(sut.Server);
        Assert.False(sut.MetricsReceived);
        Assert.False(sut.InitialLoadCompleted);
        Assert.Null(sut.LastUpdated);
    }
}
