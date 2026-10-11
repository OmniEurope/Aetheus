// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R2-111: an agent-update event reloads the loaded server's update request, agent version and
/// compatibility, so the Overview's update state ends on the new version or on the failure.
/// </summary>
public sealed class ServerDetailLoaderAgentUpdateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDetailLoaderAgentUpdateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/monitoring/servers/7/metrics", new List<ServerMetricDto>());
    }

    private static ServerDetailDto Detail(string version, AgentUpdateRequestSummaryDto? request) => new()
    {
        Id = 7,
        Name = "web-prod",
        Hostname = "10.0.0.7",
        IpAddress = "10.0.0.7",
        Status = ServerStatus.Online,
        AgentVersion = version,
        AgentUpdateRequest = request,
        CpuPercent = 42
    };

    private ServerDetailLoader NewLoader() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        Services.GetRequiredService<NavigationManager>(),
        NullLogger<ServerDetailLoader>.Instance);

    [Fact]
    public async Task AReload_TakesTheUpdateRequestAndVersion_AndKeepsTheLiveFields()
    {
        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Queued
        }));
        var loader = NewLoader();
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        var changes = 0;
        loader.OnChanged += () => changes++;

        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Failed,
            FailureDiagnostic = "download refused"
        }) with
        { CpuPercent = 3 });
        await loader.ReloadAgentUpdateAsync(7, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(1, changes);
        Assert.Equal(AgentUpdateRequestStatus.Failed, loader.Server!.AgentUpdateRequest!.Status);
        Assert.Equal("download refused", loader.Server.AgentUpdateRequest.FailureDiagnostic);
        // The heartbeat-driven fields are the live ones and stay as they were.
        Assert.Equal(42, loader.Server.CpuPercent);
    }

    // Recette R1-42: a reconnect re-reads the update request, because the confirmation sent while the
    // tab slept is never replayed; it does so even when the group re-join itself fails.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReconnect_RejoinsThenReadsTheUpdateRequestAgain(bool rejoinFails)
    {
        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Handoff
        }));
        var loader = NewLoader();
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1552", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Confirmed
        }));
        var rejoined = 0;

        var reconnect = loader.RejoinAndReloadAgentUpdateAsync(() =>
        {
            rejoined++;
            return rejoinFails ? Task.FromException(new InvalidOperationException("not connected")) : Task.CompletedTask;
        }, 7, Xunit.TestContext.Current.CancellationToken);
        if (rejoinFails)
            await Assert.ThrowsAsync<InvalidOperationException>(() => reconnect);
        else
            await reconnect;

        Assert.Equal(1, rejoined);
        Assert.Equal(AgentUpdateRequestStatus.Confirmed, loader.Server!.AgentUpdateRequest!.Status);
        Assert.Equal("1.0.1552", loader.Server.AgentVersion);
    }

    [Fact]
    public async Task AReconnectOfTheServerHub_ReadsTheUpdateRequestInsideTheCatchUp()
    {
        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Handoff
        }));
        using var status = new RealtimeSyncStatus(TimeProvider.System);
        var factory = new CapturingHubConnectionFactory(status);
        var loader = new ServerDetailLoader(
            Services.GetRequiredService<ApiClient>(),
            factory,
            Services.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance);
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        var hub = await factory.Created.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        var answer = new TaskCompletionSource<ServerDetailDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/servers/7", _ => answer.Task);
        await RaiseAsync(hub, "Reconnecting", new IOException("socket closed while the tab slept"));
        var reconnected = RaiseAsync(hub, "Reconnected", "connection-2");

        // The re-read is part of the catch-up: it is still in flight until the backend answers.
        Assert.Equal(1, status.InFlight);
        answer.SetResult(Detail("1.0.1552", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Confirmed
        }));
        await reconnected;

        Assert.Equal(0, status.InFlight);
        Assert.Equal(AgentUpdateRequestStatus.Confirmed, loader.Server!.AgentUpdateRequest!.Status);
    }

    private static Task RaiseAsync(Microsoft.AspNetCore.SignalR.Client.HubConnection hub, string name, object? argument)
    {
        var field = typeof(Microsoft.AspNetCore.SignalR.Client.HubConnection)
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var handler = (Delegate)field.GetValue(hub)!;
        return Task.WhenAll(handler.GetInvocationList().Select(single => (Task)single.DynamicInvoke(argument)!));
    }

    /// <summary>The real factory (with its catch-up reporting), aimed at a closed port, keeping the hub it built.</summary>
    private sealed class CapturingHubConnectionFactory(RealtimeSyncStatus status) : HubConnectionFactory(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://127.0.0.1:1" })
            .Build(),
        new AuthStateProvider(NSubstitute.Substitute.For<Microsoft.JSInterop.IJSRuntime>(), NullLogger<AuthStateProvider>.Instance),
        NullLogger<AuthDelegatingHandler>.Instance,
        status)
    {
        public TaskCompletionSource<Microsoft.AspNetCore.SignalR.Client.HubConnection> Created { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Microsoft.AspNetCore.SignalR.Client.HubConnection Create(
            string hubPath, Microsoft.AspNetCore.SignalR.Client.IRetryPolicy? retryPolicy = null)
        {
            var hub = base.Create(hubPath, retryPolicy);
            Created.TrySetResult(hub);
            return hub;
        }
    }

    [Fact]
    public async Task AReloadForAnotherServer_ChangesNothing()
    {
        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", null));
        var loader = NewLoader();
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        var changes = 0;
        loader.OnChanged += () => changes++;

        _handler.SetJsonResponse("api/servers/8", Detail("1.0.1552", null) with { Id = 8 });
        await loader.ReloadAgentUpdateAsync(8, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(0, changes);
        Assert.Equal("1.0.1547", loader.Server!.AgentVersion);
    }
}
